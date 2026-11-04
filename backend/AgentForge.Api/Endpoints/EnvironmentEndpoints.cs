using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Environments;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Api.Endpoints;

/// <param name="Token">Token do host agent. Obrigatório ao criar; ao editar, vazio mantém o token guardado.</param>
public record EnvironmentInput(string? Name, string? Url, string? Token, string? WorkspaceRoot, string? Tags);

/// <summary>O token nunca é devolvido: só se há um guardado.</summary>
public record EnvironmentDto(Guid Id, string Name, string Url, string WorkspaceRoot, string Tags, bool HasToken, int ProjectCount, DateTime CreatedAt);

public record DefaultEnvironmentDto(string Mode, string Description);
public record EnvironmentListDto(DefaultEnvironmentDto Default, List<EnvironmentDto> Items);

public record ToolCheckDto(string Tool, bool Ok, string Detail);
public record EnvironmentTestDto(bool Reachable, string? Error, bool WorkspaceRootExists, List<ToolCheckDto> Tools);

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

    internal static EnvironmentDto ToDto(ExecutionEnvironment e, int projectCount) =>
        new(e.Id, e.Name, e.Url, e.WorkspaceRoot, e.Tags, e.ProtectedToken.Length > 0, projectCount, e.CreatedAt);

    private static void Apply(ExecutionEnvironment e, EnvironmentInput i, ISecretProtector protector)
    {
        e.Name = i.Name!.Trim();
        e.Url = i.Url!.Trim().TrimEnd('/');
        e.WorkspaceRoot = i.WorkspaceRoot!.Trim();
        e.Tags = string.Join(", ", (i.Tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
        return new EnvironmentTestDto(true, null, rootExists, tools);
    }

    private static string FirstLine(string text) =>
        text.Split('\n', 2, StringSplitOptions.TrimEntries)[0].Trim();
}
