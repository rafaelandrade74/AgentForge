using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Endpoints;

/// <param name="Token">Token do host agent. Obrigatório ao criar; ao editar, vazio mantém o token guardado.</param>
/// <param name="MaxConcurrency">Tarefas simultâneas nesta máquina (1 a 32). Omitido: mantém o atual (1 ao criar).</param>
public record EnvironmentInput(string? Name, string? Url, string? Token, string? WorkspaceRoot, string? Tags, int? MaxConcurrency = null);

/// <summary>O token nunca é devolvido: só se há um guardado.</summary>
public record EnvironmentDto(Guid Id, string Name, string Url, string WorkspaceRoot, string Tags, bool HasToken, int ProjectCount, DateTime CreatedAt, int MaxConcurrency = 1);

public record DefaultEnvironmentDto(string Mode, string Description);
public record EnvironmentListDto(DefaultEnvironmentDto Default, List<EnvironmentDto> Items);

/// <summary>Uma linha do painel de carga: o ambiente, quantas tarefas rodam/esperam vaga e quantos agentes estão rodando agora.</summary>
public record EnvironmentLoadDto(Guid? Id, string Name, string Kind, int MaxConcurrency, int RunningTasks, int QueuedTasks, int RunningAgents, int ProjectCount);

/// <summary>Tarefa/demanda que espera a máquina do ambiente voltar (não está Blocked).</summary>
public record WaitingDto(string Kind, Guid Id, string Title, string Project, string Reason, DateTime RetryAt, int Attempts);

public record EnvironmentLoadReport(List<EnvironmentLoadDto> Environments, List<WaitingDto> Waiting);

public record ToolCheckDto(string Tool, bool Ok, string Detail);
public record EnvironmentTestDto(bool Reachable, string? Error, bool WorkspaceRootExists, List<ToolCheckDto> Tools,
    bool ContainersEnabled = false);

public static class EnvironmentEndpoints
{
    // ferramentas que os agentes costumam precisar; o host agent só roda as que estão na lista permitida dele
    private static readonly (string Tool, string[] Args)[] Probes =
        [("claude", ["--version"]), ("git", ["--version"]), ("node", ["--version"]), ("dotnet", ["--version"])];

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public static void MapEnvironmentEndpoints(this IEndpointRouteBuilder app)
    {
        // só com o login (JWT): um token de acesso do MCP não cadastra máquinas nem lê/altera tokens de host agents
        var g = app.MapGroup("/api/environments").RequireAuthorization().WithTags("Environments");

        g.MapGet("/", async (AppDbContext db, IEnvironmentLaunchers environments) =>
        {
            var items = await db.ExecutionEnvironments.AsNoTracking().OrderBy(e => e.Name).ToListAsync();
            var counts = await db.Projects.Where(p => p.EnvironmentId != null)
                .GroupBy(p => p.EnvironmentId!.Value).Select(x => new { Id = x.Key, N = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.N);
            var launcher = environments.Default.Launcher;
            return new EnvironmentListDto(new DefaultEnvironmentDto(launcher.Mode, launcher.Description),
                items.Select(e => ToDto(e, counts.GetValueOrDefault(e.Id))).ToList());
        }).WithSummary("Ambientes cadastrados e o ambiente padrão do servidor.");

        g.MapGet("/load", (AppDbContext db, IEnvironmentLoad load, IOptions<OrchestratorOptions> options, CancellationToken ct) =>
                LoadAsync(db, load, options.Value, ct))
            .WithSummary("Carga por ambiente: tarefas rodando, na fila e esperando a máquina voltar.");

        g.MapPost("/", async (EnvironmentInput input, AppDbContext db, ISecretProtector protector) =>
        {
            if (Validate(input, requireToken: true) is { } bad) return bad;
            var name = input.Name!.Trim();
            if (await db.ExecutionEnvironments.AnyAsync(e => e.Name == name))
                return Results.Conflict(new { error = $"Já existe um ambiente chamado '{name}'." });

            var environment = new ExecutionEnvironment();
            Apply(environment, input, protector);
            db.ExecutionEnvironments.Add(environment);
            await db.SaveChangesAsync();
            return Results.Created($"/api/environments/{environment.Id}", ToDto(environment, 0));
        });

        g.MapPut("/{id:guid}", async (Guid id, EnvironmentInput input, AppDbContext db, ISecretProtector protector) =>
        {
            if (Validate(input, requireToken: false) is { } bad) return bad;
            if (await db.ExecutionEnvironments.FindAsync(id) is not { } environment) return Results.NotFound();
            var name = input.Name!.Trim();
            if (await db.ExecutionEnvironments.AnyAsync(e => e.Name == name && e.Id != id))
                return Results.Conflict(new { error = $"Já existe um ambiente chamado '{name}'." });

            Apply(environment, input, protector);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(environment, await db.Projects.CountAsync(p => p.EnvironmentId == id)));
        });

        g.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            if (await db.Projects.CountAsync(p => p.EnvironmentId == id) is var used and > 0)
                return Results.Conflict(new { error = $"O ambiente está em uso por {used} projeto(s). Mude o ambiente deles antes de apagar." });
            if (await db.ProjectRoleEnvironments.CountAsync(r => r.EnvironmentId == id) is var roles and > 0)
                return Results.Conflict(new { error = $"O ambiente roda papéis (Tech Lead/Security) de {roles} projeto(s). Mude isso nos projetos antes de apagar." });
            return await db.ExecutionEnvironments.Where(e => e.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound();
        });

        g.MapPost("/{id:guid}/test", async (Guid id, AppDbContext db, IEnvironmentLaunchers environments, CancellationToken ct) =>
        {
            if (await db.ExecutionEnvironments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct) is not { } environment)
                return Results.NotFound();
            try
            {
                return Results.Ok(await TestAsync(environments.ForEnvironment(environment), ct));
            }
            catch (EnvironmentUnavailableException ex)
            {
                return Results.Ok(new EnvironmentTestDto(false, ex.Message, false, []));
            }
        }).WithSummary("Testa a conexão com o host agent e mostra as ferramentas disponíveis na máquina.");

        g.MapPost("/default/test", async (IEnvironmentLaunchers environments, CancellationToken ct) =>
            Results.Ok(await TestAsync(environments.Default, ct)))
            .WithSummary("O mesmo teste, para o ambiente padrão do servidor.");
    }

    /// <summary>Monta o painel de carga (tela Ambientes e ferramenta do MCP).</summary>
    internal static async Task<EnvironmentLoadReport> LoadAsync(AppDbContext db, IEnvironmentLoad load, OrchestratorOptions options, CancellationToken ct)
    {
        var slots = load.Slots().ToDictionary(s => s.Key);
        var environments = await db.ExecutionEnvironments.AsNoTracking().OrderBy(e => e.Name).ToListAsync(ct);
        var runningAgents = await db.AgentRuns.AsNoTracking().Where(r => r.Status == AgentRunStatus.Running)
            .GroupBy(r => r.EnvironmentId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);
        var projects = await db.Projects.AsNoTracking().GroupBy(p => p.EnvironmentId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct);

        EnvironmentLoadDto Row(Guid? id, string name, string kind, int max)
        {
            var slot = slots.GetValueOrDefault(id?.ToString() ?? EnvironmentKeys.Default);
            return new EnvironmentLoadDto(id, name, kind, max, slot?.Running ?? 0, slot?.Queued ?? 0,
                runningAgents.FirstOrDefault(r => r.Id == id)?.N ?? 0, projects.FirstOrDefault(p => p.Id == id)?.N ?? 0);
        }

        var rows = new List<EnvironmentLoadDto> { Row(null, "Padrão do servidor", "default", Math.Max(1, options.MaxConcurrency)) };
        rows.AddRange(environments.Select(e => Row(e.Id, e.Name, "host", e.MaxConcurrency)));

        var waiting = load.Waiting();
        var itemIds = waiting.Where(w => !w.IsDemand).Select(w => w.Id).ToList();
        var demandIds = waiting.Where(w => w.IsDemand).Select(w => w.Id).ToList();
        var items = await db.WorkItems.AsNoTracking().Where(w => itemIds.Contains(w.Id))
            .Join(db.Demands, w => w.DemandId, d => d.Id, (w, d) => new { w.Id, w.Title, d.ProjectId }).ToListAsync(ct);
        var demands = await db.Demands.AsNoTracking().Where(d => demandIds.Contains(d.Id)).Select(d => new { d.Id, d.Title, d.ProjectId }).ToListAsync(ct);
        var projectNames = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var entries = waiting.Select(w =>
        {
            var (title, projectId) = w.IsDemand
                ? (demands.FirstOrDefault(d => d.Id == w.Id)?.Title, demands.FirstOrDefault(d => d.Id == w.Id)?.ProjectId)
                : (items.FirstOrDefault(i => i.Id == w.Id)?.Title, items.FirstOrDefault(i => i.Id == w.Id)?.ProjectId);
            return new WaitingDto(w.IsDemand ? "demand" : "task", w.Id, title ?? "(removida)",
                projectId is { } p ? projectNames.GetValueOrDefault(p, "") : "", w.Reason, w.RetryAt, w.Attempts);
        }).ToList();
        return new EnvironmentLoadReport(rows, entries);
    }

    internal static EnvironmentDto ToDto(ExecutionEnvironment e, int projectCount) =>
        new(e.Id, e.Name, e.Url, e.WorkspaceRoot, e.Tags, e.ProtectedToken.Length > 0, projectCount, e.CreatedAt, e.MaxConcurrency);

    private static void Apply(ExecutionEnvironment e, EnvironmentInput i, ISecretProtector protector)
    {
        e.Name = i.Name!.Trim();
        e.Url = i.Url!.Trim().TrimEnd('/');
        e.WorkspaceRoot = i.WorkspaceRoot!.Trim();
        e.Tags = string.Join(", ", (i.Tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (i.MaxConcurrency is { } max) e.MaxConcurrency = max;
        if (!string.IsNullOrWhiteSpace(i.Token)) e.ProtectedToken = protector.Protect(i.Token.Trim());
    }

    private static IResult? Validate(EnvironmentInput i, bool requireToken)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return Invalid("name", "Informe o nome.");
        if (i.Name.Trim().Length > 100) return Invalid("name", "O nome pode ter até 100 caracteres.");
        if (!Uri.TryCreate(i.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            return Invalid("url", "Informe o endereço do host agent (http://máquina:8787), sem usuário e senha na URL.");
        if (string.IsNullOrWhiteSpace(i.WorkspaceRoot))
            return Invalid("workspaceRoot", "Informe a pasta, nessa máquina, onde ficam os clones (ex.: C:/agentforge ou /home/me/agentforge).");
        if (requireToken && string.IsNullOrWhiteSpace(i.Token)) return Invalid("token", "Informe o token do host agent.");
        if (i.MaxConcurrency is < 1 or > 32) return Invalid("maxConcurrency", "A concorrência máxima vai de 1 a 32 tarefas.");
        return null;
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static async Task<EnvironmentTestDto> TestAsync(EnvironmentTarget target, CancellationToken ct)
    {
        var tools = new List<ToolCheckDto>();
        foreach (var (tool, args) in Probes)
        {
            CompletedProcess result;
            try
            {
                var scratch = await target.Launcher.ScratchDirectoryAsync(ct);
                result = await target.Launcher.RunAsync(new LaunchSpec(tool, args, scratch), ProbeTimeout, ct);
            }
            catch (ProcessStartException ex)
            {
                // sem resposta do host agent: não adianta testar as outras ferramentas
                return new EnvironmentTestDto(false, ex.Message, false, tools);
            }

            tools.Add(result switch
            {
                { Started: false } => new ToolCheckDto(tool, false, "não disponível (ausente ou fora da lista permitida do host agent)"),
                { TimedOut: true } => new ToolCheckDto(tool, false, "sem resposta no tempo limite"),
                { ExitCode: 0 } => new ToolCheckDto(tool, true, FirstLine(result.Stdout)),
                _ => new ToolCheckDto(tool, false, FirstLine(result.Stderr) is { Length: > 0 } e ? e : $"saiu com código {result.ExitCode}"),
            });
        }

        var rootExists = false;
        if (target.WorkspaceRoot is { Length: > 0 } root)
            rootExists = await target.Launcher.DirectoryExistsAsync(root.Replace('\\', '/'), ct);
        // containers por tarefa (perfis de stack): o host agent informa se habilitou o Docker
        var containers = target.Launcher is RemoteProcessLauncher remote && (await remote.InfoAsync(ct)).ContainersEnabled;
        return new EnvironmentTestDto(true, null, rootExists, tools, containers);
    }

    private static string FirstLine(string text) =>
        text.Split('\n', 2, StringSplitOptions.TrimEntries)[0].Trim();
}
