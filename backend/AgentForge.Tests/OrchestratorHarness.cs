using System.Collections.Concurrent;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Workflow;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentForge.Tests;

/// <summary>
/// CLI falso para o orquestrador: cada papel responde com um trecho de shell que imprime "result:{json}".
/// O Dev faz um commit de verdade no worktree. Os testes ajustam as respostas por papel.
/// </summary>
public class ScriptedAdapter : ICliAgentAdapter
{
    public const string Approved = """{"verdict":"approved","findings":[]}""";
    public const string QaPassed = """{"verdict":"passed","criteria":[]}""";

    public string Name => "fake";
    public string Executable => "sh";

    public ConcurrentQueue<AgentRequest> Requests { get; } = new();
    public string PlanJson { get; set; } =
        """{"tasks":[{"key":"a","title":"Tarefa A","description":"d","acceptanceCriteria":["x"],"dependsOn":[]}]}""";

    /// <summary>Respostas JSON em fila por papel (uma por execução); acabando a fila, usa o valor padrão.</summary>
    public ConcurrentDictionary<AgentRole, ConcurrentQueue<string>> Queued { get; } = new();
    public ConcurrentDictionary<AgentRole, string> Defaults { get; } = new();
    /// <summary>Trecho de shell que substitui a resposta normal (ex.: "exec sleep 120" para travar).</summary>
    public ConcurrentDictionary<AgentRole, string> RawShell { get; } = new();
    /// <summary>Script do Dev calculado por pedido (ex.: todos escrevem o mesmo arquivo, para provocar conflito).</summary>
    public Func<AgentRequest, string>? DevShell { get; set; }

    public void Enqueue(AgentRole role, params string[] responses)
    {
        var q = Queued.GetOrAdd(role, _ => new());
        foreach (var r in responses) q.Enqueue(r);
    }

    public int Count(AgentRole role) => Requests.Count(r => r.Role == role);

    public IReadOnlyList<string> BuildArguments(AgentRequest request)
    {
        Requests.Enqueue(request);
        return ["-c", "cat >/dev/null; " + Script(request)];
    }

    private string Script(AgentRequest request)
    {
        if (RawShell.TryGetValue(request.Role, out var raw)) return raw;

        if (request.Role == AgentRole.Dev)
        {
            if (DevShell is not null) return DevShell(request);
            if (Queued.TryGetValue(AgentRole.Dev, out var devQ) && devQ.TryDequeue(out var junk))
                return $"echo '{junk}'";   // saída sem JSON, para testar a reexecução
            // o arquivo leva o título da tarefa (1ª linha do prompt: "Tarefa: X"), para os testes saberem quem criou o quê
            var title = request.Prompt.Split('\n')[0].Replace("Tarefa:", "").Trim();
            var slug = new string(title.Where(char.IsLetterOrDigit).ToArray());
            return $$"""f="dev-{{slug}}-$(date +%s%N).txt"; echo x > "$f"; git add -A; git commit -qm "dev: $f"; H=$(git rev-parse HEAD); printf 'result:{"summary":"ok","files":["%s"],"commit":"%s"}\n' "$f" "$H" """;
        }

        string? json = null;
        if (Queued.TryGetValue(request.Role, out var q)) q.TryDequeue(out json);
        json ??= Defaults.TryGetValue(request.Role, out var d) ? d : request.Role switch
        {
            AgentRole.ScrumMaster => PlanJson,
            AgentRole.Qa => QaPassed,
            _ => Approved,
        };
        return $"printf '%s\\n' 'result:{json}'";
    }

    public AgentEventData? ParseEvent(string line) => line.StartsWith("evt:") ? new("log", line[4..]) : null;

    public AgentResult ParseResult(IReadOnlyList<string> stdoutLines, int exitCode)
    {
        var text = stdoutLines.LastOrDefault(l => l.StartsWith("result:"))?[7..] ?? string.Join("\n", stdoutLines);
        return new AgentResult(text, JsonBlock.ExtractLast(text), 0.01m, exitCode != 0);
    }
}

/// <summary>Sobe o orquestrador de verdade (fila, trabalhadores, banco, git) sem a API HTTP.</summary>
public sealed class OrchestratorHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<IHostedService> _hosted;

    public ScriptedAdapter Adapter { get; }
    public string ConnectionString { get; }
    public string ReposRoot { get; }

    private OrchestratorHarness(ServiceProvider provider, ScriptedAdapter adapter, string cs, string root)
    {
        _provider = provider;
        Adapter = adapter;
        ConnectionString = cs;
        ReposRoot = root;
        _hosted = provider.GetServices<IHostedService>().ToList();
    }

    public static async Task<OrchestratorHarness> StartAsync(string connectionString, string reposRoot,
        ScriptedAdapter? adapter = null, int concurrency = 1, Action<IServiceCollection>? configure = null,
        Dictionary<string, string?>? settings = null)
    {
        adapter ??= new ScriptedAdapter();
        var values = new Dictionary<string, string?>
        {
            ["Workspaces:Root"] = reposRoot,
            ["Orchestrator:MaxConcurrency"] = concurrency.ToString(),
        };
        foreach (var (k, v) in settings ?? []) values[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton<ICliAgentAdapter>(adapter);
        configure?.Invoke(services);   // ex.: IEnvironmentLaunchers de teste (registrado antes do padrão)
        services.AddAgentRunner();
        services.AddWorkspaces(config);
        services.AddOrchestrator(config);
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            await RoleConfigSeeder.SeedAsync(db, Path.Combine(reposRoot, "sem-prompts"));
            await db.AgentRoleConfigs.ExecuteUpdateAsync(s => s.SetProperty(c => c.AdapterName, "fake"));
        }

        var harness = new OrchestratorHarness(provider, adapter, connectionString, reposRoot);
        foreach (var h in harness._hosted) await h.StartAsync(CancellationToken.None);
        return harness;
    }

    public AsyncServiceScope Scope() => _provider.CreateAsyncScope();

    /// <summary>Serviços singleton do orquestrador (fila, carga por ambiente).</summary>
    public IServiceProvider Services => _provider;

    public async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Scope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<T> WithActions<T>(Func<WorkflowActions, Task<T>> action)
    {
        await using var scope = Scope();
        return await action(scope.ServiceProvider.GetRequiredService<WorkflowActions>());
    }

    public async Task<(Project Project, Demand Demand)> CreateDemandAsync(string originUrl, bool autoMode)
    {
        return await WithDb(async db =>
        {
            var project = new Project { Name = "P", RepositoryUrl = originUrl, BaseBranch = "main", AutoMode = autoMode };
            var demand = new Demand { ProjectId = project.Id, Title = "Filtro por status", Description = "Adicionar filtro" };
            db.AddRange(project, demand);
            await db.SaveChangesAsync();
            return (project, demand);
        });
    }

    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return await condition();
    }

    public Task<bool> UntilItems(Guid demandId, Func<List<WorkItem>, bool> predicate, int seconds = 90) =>
        UntilAsync(() => WithDb(async db => predicate(await db.WorkItems.AsNoTracking().Where(w => w.DemandId == demandId).ToListAsync())), seconds);

    public async ValueTask DisposeAsync()
    {
        foreach (var h in Enumerable.Reverse(_hosted))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await h.StopAsync(cts.Token); } catch (OperationCanceledException) { }
        }
        await _provider.DisposeAsync();
    }
}

public static class GitOrigin
{
    /// <summary>Cria um repositório bare com um commit inicial em main e devolve o caminho.</summary>
    public static async Task<string> CreateAsync(string tempDir)
    {
        var seed = Path.Combine(tempDir, "seed");
        var origin = Path.Combine(tempDir, "origin.git");
        Directory.CreateDirectory(seed);
        await GitCli.RunAsync(seed, ["init", "-b", "main"], default);
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "base\n");
        await GitCli.RunAsync(seed, ["add", "."], default);
        await GitCli.RunAsync(seed, ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "inicial"], default);
        await GitCli.RunAsync(tempDir, ["clone", "--bare", seed, origin], default);
        return origin;
    }

    public static async Task<string> HeadAsync(string origin, string branch = "main") =>
        (await GitCli.RunAsync(origin, ["rev-parse", branch], default)).Trim();

    public static async Task<bool> HasBranchAsync(string repo, string branch) =>
        (await GitCli.RunAsync(repo, ["branch", "--list", branch], default)).Trim().Length > 0;
}
