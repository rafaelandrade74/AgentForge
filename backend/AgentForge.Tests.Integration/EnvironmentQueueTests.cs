using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests.Integration;

public class EnvironmentGateTests
{
    [Fact]
    public void Work_beyond_the_limit_is_parked_and_comes_back_when_a_slot_opens()
    {
        var gate = new EnvironmentGate<string>();

        Assert.True(gate.TryEnter("a", 2, "t1"));
        Assert.True(gate.TryEnter("a", 2, "t2"));
        Assert.False(gate.TryEnter("a", 2, "t3"));
        Assert.False(gate.TryEnter("a", 2, "t4"));
        Assert.Equal(new EnvironmentSlotInfo("a", 2, 2, 2), gate.Snapshot().Single());

        var back = gate.Leave("a");

        Assert.Equal(["t3", "t4"], back);                       // volta para a fila; quem couber entra, o resto estaciona de novo
        Assert.True(gate.TryEnter("a", 2, "t3"));
        Assert.False(gate.TryEnter("a", 2, "t4"));
    }

    [Fact]
    public void Environments_do_not_block_each_other_and_the_limit_can_change_on_the_fly()
    {
        var gate = new EnvironmentGate<string>();

        Assert.True(gate.TryEnter("a", 1, "a1"));
        Assert.False(gate.TryEnter("a", 1, "a2"));
        Assert.True(gate.TryEnter("b", 1, "b1"));              // outra máquina livre: anda
        Assert.True(gate.TryEnter("a", 2, "a2"));               // o limite subiu na tela Ambientes
    }

    [Fact]
    public void A_limit_below_one_is_treated_as_one_and_leaving_an_unknown_key_is_harmless()
    {
        var gate = new EnvironmentGate<string>();

        Assert.True(gate.TryEnter("a", 0, "t1"));
        Assert.False(gate.TryEnter("a", 0, "t2"));
        Assert.Empty(gate.Leave("nunca-usado"));
    }

    [Fact]
    public async Task Parking_and_leaving_concurrently_never_loses_work()
    {
        var gate = new EnvironmentGate<int>();
        var done = new System.Collections.Concurrent.ConcurrentBag<int>();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<int>(Enumerable.Range(0, 200));

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (done.Count < 200)
            {
                if (!queue.TryDequeue(out var work)) { await Task.Delay(1); continue; }
                if (!gate.TryEnter("a", 2, work)) continue;
                await Task.Yield();
                done.Add(work);
                foreach (var parked in gate.Leave("a")) queue.Enqueue(parked);
            }
        })).ToArray();

        await Task.WhenAny(Task.WhenAll(workers), Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Equal(200, done.Distinct().Count());
    }
}

/// <summary>Máquina de teste: roda local, com pasta temporária própria e uma chave para "derrubá-la".</summary>
public sealed class TestMachine(string scratch) : IProcessLauncher
{
    private readonly LocalProcessLauncher _inner = new();
    public volatile bool Down;
    public string Mode => "host";
    public string Description => "máquina de teste " + scratch;

    private void Check()
    {
        if (Down) throw new HostUnreachableException("Host agent inacessível em http://teste: conexão recusada");
    }

    public Task<string> ScratchDirectoryAsync(CancellationToken ct)
    {
        Check();
        Directory.CreateDirectory(scratch);
        return Task.FromResult(scratch);
    }

    public Task<IRunningProcess> StartAsync(LaunchSpec spec, CancellationToken ct)
    {
        Check();
        return _inner.StartAsync(spec, ct);
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct) { Check(); return _inner.DirectoryExistsAsync(path, ct); }
    public Task CreateDirectoryAsync(string path, CancellationToken ct) { Check(); return _inner.CreateDirectoryAsync(path, ct); }
    public Task DeleteDirectoryAsync(string path, CancellationToken ct) { Check(); return _inner.DeleteDirectoryAsync(path, ct); }
    public Task WriteFileAsync(string path, string content, bool executable, CancellationToken ct) { Check(); return _inner.WriteFileAsync(path, content, executable, ct); }
}

/// <summary>Ambientes de teste: cada um é uma <see cref="TestMachine"/> com a sua pasta de clones.</summary>
public sealed class TestEnvironments(string temp) : IEnvironmentLaunchers
{
    private readonly Dictionary<Guid, (TestMachine Machine, string Root, string Name)> _machines = new();
    public EnvironmentTarget Default { get; } = new(new TestMachine(Path.Combine(temp, "scratch-default")), null, null, "Padrão");

    public TestMachine Add(Guid id, string name)
    {
        var machine = new TestMachine(Path.Combine(temp, "scratch-" + name));
        _machines[id] = (machine, Path.Combine(temp, "root-" + name), name);
        return machine;
    }

    public string RootOf(Guid id) => _machines[id].Root;

    public Task<EnvironmentTarget> ResolveAsync(Guid? environmentId, CancellationToken ct) =>
        environmentId is { } id && _machines.TryGetValue(id, out var m)
            ? Task.FromResult(new EnvironmentTarget(m.Machine, m.Root, id, m.Name))
            : environmentId is null ? Task.FromResult(Default) : throw new EnvironmentUnavailableException("Ambiente inexistente.");

    public EnvironmentTarget ForEnvironment(ExecutionEnvironment environment) => ResolveAsync(environment.Id, default).Result;
}

public sealed class EnvironmentQueueTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-queue-" + Guid.NewGuid().ToString("N"));
    private string _origin = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        if (!OperatingSystem.IsWindows()) _origin = await GitOrigin.CreateAsync(_temp);
    }

    public Task DisposeAsync()
    {
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<OrchestratorHarness> Start(TestEnvironments envs, ScriptedAdapter adapter, int concurrency, double retrySeconds = 0.2,
        Action<IServiceCollection>? more = null) =>
        await OrchestratorHarness.StartAsync(pg.NewConnectionString(), Path.Combine(_temp, "root-default"), adapter, concurrency,
            services =>
            {
                services.AddSingleton<IEnvironmentLaunchers>(envs);
                more?.Invoke(services);
            },
            new Dictionary<string, string?>
            {
                ["Orchestrator:EnvironmentRetryInitialSeconds"] = retrySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Orchestrator:EnvironmentRetryMaxSeconds"] = "1",
            });

    private static async Task<Guid> AddEnvironment(OrchestratorHarness h, string name, int max, Guid id = default)
    {
        if (id == default) id = Guid.NewGuid();
        await h.WithDb(async db =>
        {
            db.ExecutionEnvironments.Add(new ExecutionEnvironment
            {
                Id = id, Name = name + "-" + id.ToString("N")[..6], Url = "http://x", WorkspaceRoot = "/x", ProtectedToken = "x", MaxConcurrency = max,
            });
            await db.SaveChangesAsync();
            return 0;
        });
        return id;
    }

    private async Task<(Project Project, Demand Demand)> Project(OrchestratorHarness h, Guid? env, string name = "P")
    {
        return await h.WithDb(async db =>
        {
            var project = new Project { Name = name, RepositoryUrl = _origin, BaseBranch = "main", AutoMode = true, EnvironmentId = env };
            var demand = new Demand { ProjectId = project.Id, Title = "Demanda " + name, Description = "d" };
            db.AddRange(project, demand);
            await db.SaveChangesAsync();
            return (project, demand);
        });
    }

    // ------------------------------------------------------------------ concorrência por ambiente

    [PosixFact]
    public async Task Each_environment_runs_at_most_its_own_limit_while_other_machines_work_in_parallel()
    {
        var envs = new TestEnvironments(_temp);
        var adapter = new ScriptedAdapter();
        await using var h = await Start(envs, adapter, concurrency: 6);
        var a = await AddEnvironment(h, "A", max: 1);
        var b = await AddEnvironment(h, "B", max: 1);
        envs.Add(a, "A");
        envs.Add(b, "B");
        var (a1, d1) = await Project(h, a, "A1");
        var (a2, d2) = await Project(h, a, "A2");
        var (b1, d3) = await Project(h, b, "B1");

        // cada Dev registra quantos Dev estão rodando na sua máquina e no total (marcadores em pastas)
        var tag = new Dictionary<Guid, string> { [a1.Id] = "A", [a2.Id] = "A", [b1.Id] = "B" };
        var marks = Path.Combine(_temp, "marks");
        Directory.CreateDirectory(marks);
        adapter.DevShell = req =>
        {
            var t = tag[req.ProjectId!.Value];
            return $$"""
                mkdir -p '{{marks}}/{{t}}' '{{marks}}/all'
                touch '{{marks}}/{{t}}/'$$ '{{marks}}/all/'$$
                echo $(ls '{{marks}}/{{t}}' | wc -l) >> '{{marks}}/max-{{t}}'
                echo $(ls '{{marks}}/all' | wc -l) >> '{{marks}}/max-all'
                sleep 1.5
                rm -f '{{marks}}/{{t}}/'$$ '{{marks}}/all/'$$
                f="dev-$(date +%s%N).txt"; echo x > "$f"; git add -A; git commit -qm "dev: $f"; H=$(git rev-parse HEAD)
                printf 'result:{"summary":"ok","files":["%s"],"commit":"%s"}\n' "$f" "$H"
                """;
        };

        foreach (var d in new[] { d1, d2, d3 }) await h.WithActions(x => x.StartDemandAsync(d.Id));
        foreach (var d in new[] { d1, d2, d3 })
            Assert.True(await h.UntilItems(d.Id, items => items.Count == 1 && items.All(i => i.State == WorkItemState.Done), 120));

        int Max(string file) => File.ReadAllLines(Path.Combine(marks, file)).Select(int.Parse).Max();
        Assert.Equal(1, Max("max-A"));        // A tem limite 1: as duas tarefas dela nunca rodam juntas
        Assert.Equal(1, Max("max-B"));
        Assert.Equal(2, Max("max-all"));      // mas A e B trabalham ao mesmo tempo
    }

    [PosixFact]
    public async Task The_load_snapshot_shows_running_and_queued_tasks_per_environment()
    {
        var envs = new TestEnvironments(_temp);
        var adapter = new ScriptedAdapter { DevShell = _ => "sleep 4" };   // segura a vaga
        await using var h = await Start(envs, adapter, concurrency: 4);
        var a = await AddEnvironment(h, "A", max: 1);
        envs.Add(a, "A");
        var (_, d1) = await Project(h, a, "A1");
        var (_, d2) = await Project(h, a, "A2");
        var load = h.Services.GetRequiredService<IEnvironmentLoad>();

        await h.WithActions(x => x.StartDemandAsync(d1.Id));
        await h.WithActions(x => x.StartDemandAsync(d2.Id));

        Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(
            load.Slots().Any(s => s.Key == a.ToString() && s.Running == 1 && s.Queued >= 1)), 60), "uma roda, a outra espera vaga");
        var slot = load.Slots().Single(s => s.Key == a.ToString());
        Assert.Equal(1, slot.Limit);
    }

    // ------------------------------------------------------------------ máquina fora do ar

    [PosixFact]
    public async Task A_machine_that_goes_down_does_not_block_the_task_and_it_finishes_when_the_machine_returns()
    {
        var envs = new TestEnvironments(_temp);
        var adapter = new ScriptedAdapter();
        await using var h = await Start(envs, adapter, concurrency: 2);
        var a = await AddEnvironment(h, "A", max: 1);
        var machine = envs.Add(a, "A");
        var (project, demand) = await Project(h, a);
        var load = h.Services.GetRequiredService<IEnvironmentLoad>();

        // a tarefa já existe e está em desenvolvimento quando a máquina cai
        Guid taskId = default;
        await h.WithDb(async db =>
        {
            demand = await db.Demands.FirstAsync(d => d.Id == demand.Id);
            demand.Status = DemandStatus.InProgress;
            var item = new WorkItem { DemandId = demand.Id, Title = "Tarefa A", AcceptanceCriteria = "x", State = WorkItemState.InDevelopment };
            db.WorkItems.Add(item);
            await db.SaveChangesAsync();
            taskId = item.Id;
            return 0;
        });
        machine.Down = true;
        h.Services.GetRequiredService<IWorkQueue>().EnqueueWorkItem(taskId);

        Assert.True(await OrchestratorHarness.UntilAsync(() => h.WithDb(async db =>
            (await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == taskId)).WaitingReason is not null), 30), "avisa que espera o ambiente");
        var waiting = await h.WithDb(db => db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == taskId));
        Assert.Equal(WorkItemState.InDevelopment, waiting.State);          // continua onde estava
        Assert.Null(waiting.BlockedReason);
        Assert.Contains("Aguardando o ambiente voltar", waiting.WaitingReason);
        Assert.Contains("inacessível", waiting.WaitingReason);
        Assert.Contains(load.Waiting(), w => w.Id == taskId && w.Attempts >= 1);

        await Task.Delay(1500);                                              // várias tentativas com a máquina ainda fora
        Assert.Equal(WorkItemState.InDevelopment, (await h.WithDb(db => db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == taskId))).State);

        machine.Down = false;                                                // a máquina volta
        Assert.True(await h.UntilItems(demand.Id, items => items.All(i => i.State == WorkItemState.Done), 90), "conclui sozinha, sem ação humana");
        var done = await h.WithDb(db => db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == taskId));
        Assert.Null(done.WaitingReason);
        Assert.Empty(load.Waiting());
    }

    private sealed class RecordingPublisher(bool fail = false) : IBranchPublisher
    {
        public System.Collections.Concurrent.ConcurrentQueue<Guid> Published { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<Guid> PublishedDemands { get; } = new();

        public Task PublishAsync(Guid workItemId, CancellationToken ct)
        {
            Published.Enqueue(workItemId);
            return fail ? throw new InvalidOperationException("rede caiu") : Task.CompletedTask;
        }

        public Task PublishDemandAsync(Guid demandId, CancellationToken ct)
        {
            PublishedDemands.Enqueue(demandId);
            return fail ? throw new InvalidOperationException("rede caiu") : Task.CompletedTask;
        }
    }

    [PosixFact]
    public Task Reaching_done_publishes_the_branch_once() => PublishOnDone(publisherFails: false);

    [PosixFact]
    public Task A_failing_publisher_never_undoes_done() => PublishOnDone(publisherFails: true);

    private async Task PublishOnDone(bool publisherFails)
    {
        var envs = new TestEnvironments(_temp);
        var publisher = new RecordingPublisher(publisherFails);
        await using var h = await Start(envs, new ScriptedAdapter(), concurrency: 2, more: s => s.AddSingleton<IBranchPublisher>(publisher));
        var a = await AddEnvironment(h, "A", max: 1);
        envs.Add(a, "A");
        var (_, demand) = await Project(h, a);

        Guid taskId = default;
        await h.WithDb(async db =>
        {
            demand = await db.Demands.FirstAsync(d => d.Id == demand.Id);
            demand.Status = DemandStatus.InProgress;
            var item = new WorkItem { DemandId = demand.Id, Title = "Tarefa A", AcceptanceCriteria = "x", State = WorkItemState.InDevelopment };
            db.WorkItems.Add(item);
            await db.SaveChangesAsync();
            taskId = item.Id;
            return 0;
        });
        h.Services.GetRequiredService<IWorkQueue>().EnqueueWorkItem(taskId);

        Assert.True(await h.UntilItems(demand.Id, items => items.All(i => i.State == WorkItemState.Done), 90), "conclui");
        Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(!publisher.PublishedDemands.IsEmpty), 10), "publicou o branch da demanda");
        Assert.Equal([demand.Id], publisher.PublishedDemands.ToArray());
    }

    [PosixFact]
    public async Task A_usage_limit_parks_the_task_until_the_agent_frees_up_without_failing_or_spending_runs()
    {
        var envs = new TestEnvironments(_temp);
        var adapter = new ScriptedAdapter();
        await using var h = await Start(envs, adapter, concurrency: 2);
        var a = await AddEnvironment(h, "A", max: 1);
        envs.Add(a, "A");
        var (_, demand) = await Project(h, a);
        var load = h.Services.GetRequiredService<IEnvironmentLoad>();
        var tracker = h.Services.GetRequiredService<IUsageLimitTracker>();

        Guid taskId = default;
        await h.WithDb(async db =>
        {
            demand = await db.Demands.FirstAsync(d => d.Id == demand.Id);
            demand.Status = DemandStatus.InProgress;
            var item = new WorkItem { DemandId = demand.Id, Title = "Tarefa A", AcceptanceCriteria = "x", State = WorkItemState.InDevelopment };
            db.WorkItems.Add(item);
            await db.SaveChangesAsync();
            taskId = item.Id;
            return 0;
        });

        // a conta esgota e libera em ~4s
        var resets = DateTime.UtcNow.AddSeconds(4);
        adapter.LimitToReturn = new UsageLimit(resets, resets, UsageLimitKinds.Session, "You've hit your limit · resets 3pm");
        h.Services.GetRequiredService<IWorkQueue>().EnqueueWorkItem(taskId);

        Assert.True(await OrchestratorHarness.UntilAsync(() => h.WithDb(async db =>
            (await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == taskId)).WaitingReason is not null), 30), "avisa que espera o agente");
        var waiting = await h.WithDb(db => db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == taskId));
        Assert.Equal(WorkItemState.InDevelopment, waiting.State);          // continua onde estava, não vira Blocked
        Assert.Null(waiting.BlockedReason);
        Assert.Contains("Aguardando o agente liberar", waiting.WaitingReason);
        Assert.Contains("limite da janela de uso", waiting.WaitingReason);
        var entry = Assert.Single(load.Waiting(), w => w.Id == taskId);
        Assert.NotNull(entry.UsageLimit);
        Assert.Equal(resets, entry.UsageLimit!.ResetsAt);
        Assert.Contains(tracker.Snapshot(), s => s.EnvironmentKey == a.ToString() && s.Adapter == adapter.Name);
        Assert.Equal(1, adapter.Count(AgentRole.Dev));                       // uma execução só: o limite não gasta tentativas

        adapter.LimitToReturn = null;                                        // o agente libera
        Assert.True(await h.UntilItems(demand.Id, items => items.All(i => i.State == WorkItemState.Done), 90), "retoma e conclui sozinha");
        var done = await h.WithDb(db => db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == taskId));
        Assert.Null(done.WaitingReason);
        Assert.Empty(load.Waiting());
        Assert.Empty(tracker.Snapshot());                                    // a sonda passou: limite limpo
    }

    [PosixFact]
    public async Task A_demand_waits_for_the_machine_instead_of_failing()
    {
        var envs = new TestEnvironments(_temp);
        await using var h = await Start(envs, new ScriptedAdapter(), concurrency: 2);
        var a = await AddEnvironment(h, "A", max: 1);
        var machine = envs.Add(a, "A");
        var (_, demand) = await Project(h, a);
        machine.Down = true;

        await h.WithActions(x => x.StartDemandAsync(demand.Id));

        var load = h.Services.GetRequiredService<IEnvironmentLoad>();
        Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(load.Waiting().Any(w => w.IsDemand && w.Id == demand.Id)), 30));
        Assert.Equal(DemandStatus.Refining, (await h.WithDb(db => db.Demands.AsNoTracking().FirstAsync(d => d.Id == demand.Id))).Status);

        machine.Down = false;
        Assert.True(await h.UntilItems(demand.Id, items => items.Count == 1 && items.All(i => i.State == WorkItemState.Done), 120));
        Assert.True(await OrchestratorHarness.UntilAsync(async () =>
            (await h.WithDb(db => db.Demands.AsNoTracking().FirstAsync(d => d.Id == demand.Id))).Status == DemandStatus.Done, 30));
    }

    [PosixFact]
    public async Task Other_environments_keep_working_while_one_machine_is_down()
    {
        var envs = new TestEnvironments(_temp);
        await using var h = await Start(envs, new ScriptedAdapter(), concurrency: 4);
        var a = await AddEnvironment(h, "A", max: 1);
        var b = await AddEnvironment(h, "B", max: 1);
        var down = envs.Add(a, "A");
        envs.Add(b, "B");
        var (_, dDown) = await Project(h, a, "Caida");
        var (_, dOk) = await Project(h, b, "Viva");
        down.Down = true;

        await h.WithActions(x => x.StartDemandAsync(dDown.Id));
        await h.WithActions(x => x.StartDemandAsync(dOk.Id));

        Assert.True(await h.UntilItems(dOk.Id, items => items.Count == 1 && items.All(i => i.State == WorkItemState.Done), 120));
        Assert.Equal(DemandStatus.Refining, (await h.WithDb(db => db.Demands.AsNoTracking().FirstAsync(d => d.Id == dDown.Id))).Status);
    }

    // ------------------------------------------------------------------ ambiente por papel

    [PosixFact]
    public async Task Tech_lead_and_security_run_on_the_light_machine_without_the_repo_while_dev_and_qa_stay_on_the_stack_machine()
    {
        var envs = new TestEnvironments(_temp);
        var adapter = new ScriptedAdapter();
        await using var h = await Start(envs, adapter, concurrency: 2);   // revisões em paralelo
        var stack = await AddEnvironment(h, "stack", max: 2);
        var light = await AddEnvironment(h, "leve", max: 2);
        envs.Add(stack, "stack");
        envs.Add(light, "leve");
        var (project, demand) = await Project(h, stack);
        await h.WithDb(async db =>
        {
            db.ProjectRoleEnvironments.AddRange(
                new ProjectRoleEnvironment { ProjectId = project.Id, Role = AgentRole.TechLead, EnvironmentId = light },
                new ProjectRoleEnvironment { ProjectId = project.Id, Role = AgentRole.Security, EnvironmentId = light });
            await db.SaveChangesAsync();
            return 0;
        });
        var where = Path.Combine(_temp, "pwd");
        Directory.CreateDirectory(where);
        foreach (var role in new[] { AgentRole.TechLead, AgentRole.Security, AgentRole.Qa })
        {
            var json = role == AgentRole.Qa ? ScriptedAdapter.QaPassed : ScriptedAdapter.Approved;
            adapter.RawShell[role] = $"pwd > '{where}/{role}'; printf '%s\\n' 'result:{json}'";
        }

        await h.WithActions(x => x.StartDemandAsync(demand.Id));
        Assert.True(await h.UntilItems(demand.Id, items => items.Count == 1 && items.All(i => i.State == WorkItemState.Done), 120));

        var lightScratch = Path.Combine(_temp, "scratch-leve");
        foreach (var role in new[] { AgentRole.TechLead, AgentRole.Security })
        {
            Assert.Equal(lightScratch, (await File.ReadAllTextAsync($"{where}/{role}")).Trim());     // sem o worktree
            var request = adapter.Requests.First(r => r.Role == role);
            Assert.True(request.EnvironmentOverride);
            Assert.Contains("sem o repositório", request.Prompt);                                   // o prompt avisa
            Assert.Contains("```diff", request.Prompt);                                             // e traz o diff
        }
        var qa = (await File.ReadAllTextAsync($"{where}/{AgentRole.Qa}")).Trim();
        Assert.StartsWith(envs.RootOf(stack), qa);                                                  // QA no worktree do ambiente da stack
        Assert.False(adapter.Requests.First(r => r.Role == AgentRole.Qa).EnvironmentOverride);
        Assert.DoesNotContain("sem o repositório", adapter.Requests.First(r => r.Role == AgentRole.Dev).Prompt);

        // o histórico diz onde cada papel rodou
        var runs = await h.WithDb(db => db.AgentRuns.AsNoTracking().ToListAsync());
        Assert.All(runs.Where(r => r.Role is AgentRole.TechLead or AgentRole.Security), r => Assert.Equal(light, r.EnvironmentId));
        Assert.All(runs.Where(r => r.Role is AgentRole.Dev or AgentRole.Qa or AgentRole.ScrumMaster), r => Assert.Equal(stack, r.EnvironmentId));
    }

    [PosixFact]
    public async Task The_light_machine_being_down_defers_the_review_instead_of_blocking_the_task()
    {
        var envs = new TestEnvironments(_temp);
        await using var h = await Start(envs, new ScriptedAdapter(), concurrency: 1);
        var stack = await AddEnvironment(h, "stack", max: 1);
        var light = await AddEnvironment(h, "leve", max: 1);
        envs.Add(stack, "stack");
        var lightMachine = envs.Add(light, "leve");
        var (project, demand) = await Project(h, stack);
        await h.WithDb(async db =>
        {
            db.ProjectRoleEnvironments.Add(new ProjectRoleEnvironment { ProjectId = project.Id, Role = AgentRole.TechLead, EnvironmentId = light });
            await db.SaveChangesAsync();
            return 0;
        });
        lightMachine.Down = true;

        await h.WithActions(x => x.StartDemandAsync(demand.Id));

        Assert.True(await OrchestratorHarness.UntilAsync(() => h.WithDb(async db =>
            await db.WorkItems.AnyAsync(w => w.DemandId == demand.Id && w.State == WorkItemState.TechLeadReview && w.WaitingReason != null)), 60));
        Assert.DoesNotContain(await h.WithDb(db => db.WorkItems.AsNoTracking().ToListAsync()), w => w.State == WorkItemState.Blocked);

        lightMachine.Down = false;
        Assert.True(await h.UntilItems(demand.Id, items => items.All(i => i.State == WorkItemState.Done), 120));
    }
}
