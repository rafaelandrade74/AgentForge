using AgentForge.Domain;
using AgentForge.Orchestrator.Workflow;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Tests;

/// <summary>Fluxo completo com Postgres real, git real e um CLI falso (sh). Só roda em Linux/macOS.</summary>
[Collection(PostgresCollection.Name)]
public sealed class OrchestratorTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-orch-" + Guid.NewGuid().ToString("N"));
    private string _origin = "";
    private string Repos => Path.Combine(_temp, "repos");
    private readonly List<OrchestratorHarness> _harnesses = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        if (!OperatingSystem.IsWindows()) _origin = await GitOrigin.CreateAsync(_temp);
    }

    public async Task DisposeAsync()
    {
        foreach (var h in _harnesses) await h.DisposeAsync();
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
    }

    private async Task<OrchestratorHarness> StartAsync(string? cs = null, ScriptedAdapter? adapter = null, int concurrency = 1)
    {
        var h = await OrchestratorHarness.StartAsync(cs ?? pg.NewConnectionString(), Repos, adapter, concurrency);
        _harnesses.Add(h);
        return h;
    }

    private static Task<WorkItem> Item(OrchestratorHarness h, Guid demandId, string title = "Tarefa A") =>
        h.WithDb(db => db.WorkItems.AsNoTracking().SingleAsync(w => w.DemandId == demandId && w.Title == title));

    [PosixFact]
    public async Task Example_demand_goes_through_every_role_without_intervention()
    {
        var originBefore = await GitOrigin.HeadAsync(_origin);
        var h = await StartAsync();
        h.Adapter.PlanJson = """{"tasks":[{"key":"a","title":"Tarefa A","description":"d","acceptanceCriteria":["a1"],"dependsOn":[]},{"key":"b","title":"Tarefa B","description":"d","acceptanceCriteria":["b1"],"dependsOn":["a"]}]}""";
        var (project, demand) = await h.CreateDemandAsync(_origin, autoMode: true);

        Assert.Equal(ActionOutcome.Ok, await h.WithActions(a => a.StartDemandAsync(demand.Id)));

        Assert.True(await OrchestratorHarness.UntilAsync(() => h.WithDb(async db =>
            (await db.Demands.AsNoTracking().SingleAsync(d => d.Id == demand.Id)).Status == DemandStatus.Done)));

        var items = await h.WithDb(db => db.WorkItems.AsNoTracking().Include(w => w.Dependencies).Where(w => w.DemandId == demand.Id).ToListAsync());
        Assert.Equal(2, items.Count);
        Assert.All(items, i => { Assert.Equal(WorkItemState.Done, i.State); Assert.StartsWith("agentforge/task-", i.BranchName); });

        var runs = await h.WithDb(db => db.AgentRuns.AsNoTracking().ToListAsync());
        Assert.Equal(new[] { AgentRole.ScrumMaster, AgentRole.Dev, AgentRole.TechLead, AgentRole.Security, AgentRole.Qa },
            runs.Select(r => r.Role).Distinct().OrderBy(r => r).ToArray());
        Assert.All(runs, r => Assert.Equal(AgentRunStatus.Succeeded, r.Status));

        // B só começa depois que A terminou
        var a = items.Single(i => i.Title == "Tarefa A");
        var b = items.Single(i => i.Title == "Tarefa B");
        var lastRunOfA = runs.Where(r => r.WorkItemId == a.Id).Max(r => r.FinishedAt);
        var firstRunOfB = runs.Where(r => r.WorkItemId == b.Id).Min(r => r.StartedAt);
        Assert.True(lastRunOfA <= firstRunOfB);

        // worktrees removidos, branches (com commits) mantidos, base intacta
        var repo = Path.Combine(Repos, project.Id.ToString(), "repo");
        Assert.Empty(Directory.GetDirectories(Path.Combine(Repos, project.Id.ToString(), "worktrees")));
        Assert.True(await GitOrigin.HasBranchAsync(repo, a.BranchName!));
        Assert.True(await GitOrigin.HasBranchAsync(repo, b.BranchName!));
        Assert.Equal(originBefore, await GitOrigin.HeadAsync(_origin));
    }

    [PosixFact]
    public async Task Rework_sends_open_findings_to_the_dev_and_resolves_them()
    {
        var h = await StartAsync();
        h.Adapter.Enqueue(AgentRole.TechLead,
            """{"verdict":"changes_requested","findings":[{"file":"a.cs","line":7,"severity":"high","message":"Renomear o metodo Xyz"}]}""");
        var (_, demand) = await h.CreateDemandAsync(_origin, autoMode: true);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));

        Assert.True(await h.UntilItems(demand.Id, i => i.Count == 1 && i[0].State == WorkItemState.Done));

        var item = await Item(h, demand.Id);
        Assert.Equal(1, item.ReworkCount);
        var devPrompts = h.Adapter.Requests.Where(r => r.Role == AgentRole.Dev).Select(r => r.Prompt).ToList();
        Assert.Equal(2, devPrompts.Count);
        Assert.DoesNotContain("RETRABALHO", devPrompts[0]);
        Assert.Contains("Renomear o metodo Xyz", devPrompts[1]);
        var finding = await h.WithDb(db => db.ReviewFindings.AsNoTracking().SingleAsync());
        Assert.True(finding.Resolved);
        Assert.Equal((AgentRole.TechLead, FindingSeverity.High, "a.cs", 7), (finding.Role, finding.Severity, finding.File, finding.Line));
    }

    [PosixFact]
    public async Task Third_return_to_dev_blocks_the_task_until_a_human_retries()
    {
        var h = await StartAsync();
        var bad = """{"verdict":"changes_requested","findings":[{"message":"ainda errado"}]}""";
        h.Adapter.Defaults[AgentRole.TechLead] = bad;
        var (_, demand) = await h.CreateDemandAsync(_origin, autoMode: true);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));

        Assert.True(await h.UntilItems(demand.Id, i => i.Count == 1 && i[0].State == WorkItemState.Blocked));

        var blocked = await Item(h, demand.Id);
        Assert.Equal(3, blocked.ReworkCount);
        Assert.Contains("3 vezes", blocked.BlockedReason);
        Assert.Equal(3, h.Adapter.Count(AgentRole.Dev));

        // decisão humana: retomar. Agora o Tech Lead aprova.
        h.Adapter.Defaults[AgentRole.TechLead] = ScriptedAdapter.Approved;
        Assert.Equal(ActionOutcome.Ok, await h.WithActions(a => a.RetryAsync(blocked.Id)));
        Assert.True(await h.UntilItems(demand.Id, i => i[0].State == WorkItemState.Done));
        Assert.Equal(0, (await Item(h, demand.Id)).ReworkCount);
        Assert.Equal(ActionOutcome.InvalidState, await h.WithActions(a => a.RetryAsync(blocked.Id)));
    }

    [PosixFact]
    public async Task Qa_failure_returns_to_dev_with_the_failed_criterion()
    {
        var h = await StartAsync();
        h.Adapter.Enqueue(AgentRole.Qa,
            """{"verdict":"failed","criteria":[{"criterion":"filtro por status","passed":false,"evidence":"retornou 500"}]}""");
        var (_, demand) = await h.CreateDemandAsync(_origin, autoMode: true);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));

        Assert.True(await h.UntilItems(demand.Id, i => i.Count == 1 && i[0].State == WorkItemState.Done));

        Assert.Contains(h.Adapter.Requests.Where(r => r.Role == AgentRole.Dev).Select(r => r.Prompt),
            p => p.Contains("filtro por status") && p.Contains("retornou 500"));
    }

    [PosixFact]
    public async Task Human_approvals_gate_the_flow_when_not_in_auto_mode()
    {
        var h = await StartAsync();
        var (_, demand) = await h.CreateDemandAsync(_origin, autoMode: false);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));

        // 1ª aprovação: o plano fica esperando em Backlog
        Assert.True(await h.UntilItems(demand.Id, i => i.Count == 1 && i[0].State == WorkItemState.Backlog));
        await Task.Delay(1000);
        Assert.Equal(0, h.Adapter.Count(AgentRole.Dev));
        Assert.Equal(ActionOutcome.Ok, await h.WithActions(a => a.ApprovePlanAsync(demand.Id)));

        // 2ª aprovação: depois do QA, espera a entrega ser aprovada
        Assert.True(await h.UntilItems(demand.Id, i => i[0].State == WorkItemState.AwaitingApproval));
        var item = await Item(h, demand.Id);

        // rejeita com comentário: volta ao Dev, que recebe o comentário
        Assert.Equal(ActionOutcome.Ok, await h.WithActions(a => a.RejectAsync(item.Id, "Falta tratar lista vazia")));
        Assert.True(await OrchestratorHarness.UntilAsync(async () =>
            h.Adapter.Count(AgentRole.Dev) == 2 && (await Item(h, demand.Id)).State == WorkItemState.AwaitingApproval));
        Assert.Contains("Falta tratar lista vazia", h.Adapter.Requests.Where(r => r.Role == AgentRole.Dev).Last().Prompt);
        Assert.Equal(0, (await Item(h, demand.Id)).ReworkCount);   // rejeição humana não conta para o limite

        Assert.Equal(ActionOutcome.Ok, await h.WithActions(a => a.ApproveAsync(item.Id)));
        Assert.True(await h.UntilItems(demand.Id, i => i[0].State == WorkItemState.Done));
        Assert.Equal(ActionOutcome.InvalidState, await h.WithActions(a => a.ApproveAsync(item.Id)));
    }

    [PosixFact]
    public async Task Invalid_agent_output_is_rerun_once_then_blocks()
    {
        // uma saída inválida: reexecuta e segue
        var h = await StartAsync();
        h.Adapter.Enqueue(AgentRole.Dev, "sem json nenhum");
        var (_, demand) = await h.CreateDemandAsync(_origin, autoMode: true);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));
        Assert.True(await h.UntilItems(demand.Id, i => i.Count == 1 && i[0].State == WorkItemState.Done));
        Assert.Equal(2, h.Adapter.Count(AgentRole.Dev));
        var retryPrompt = h.Adapter.Requests.Where(r => r.Role == AgentRole.Dev).Last().Prompt;
        Assert.Contains("A tentativa anterior falhou", retryPrompt);

        // duas saídas inválidas: Blocked
        var h2 = await StartAsync();
        h2.Adapter.Enqueue(AgentRole.Qa, """{"verdict":"talvez"}""", """{"verdict":"talvez"}""");
        var (_, demand2) = await h2.CreateDemandAsync(_origin, autoMode: true);
        await h2.WithActions(a => a.StartDemandAsync(demand2.Id));
        Assert.True(await h2.UntilItems(demand2.Id, i => i.Count == 1 && i[0].State == WorkItemState.Blocked));
        Assert.Contains("verdict", (await Item(h2, demand2.Id)).BlockedReason);
        Assert.Equal(2, h2.Adapter.Count(AgentRole.Qa));
    }

    [PosixFact]
    public async Task Dev_claiming_a_nonexistent_commit_is_treated_as_invalid_output()
    {
        var h = await StartAsync();
        h.Adapter.RawShell[AgentRole.Dev] = """printf 'result:{"summary":"ok","files":[],"commit":"deadbeefdeadbeef"}\n'""";
        var (_, demand) = await h.CreateDemandAsync(_origin, autoMode: true);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));

        Assert.True(await h.UntilItems(demand.Id, i => i.Count == 1 && i[0].State == WorkItemState.Blocked));
        Assert.Contains("deadbeefdeadbeef", (await Item(h, demand.Id)).BlockedReason);
    }

    [PosixFact]
    public async Task Invalid_plan_fails_the_demand_and_can_be_restarted()
    {
        var h = await StartAsync();
        h.Adapter.PlanJson = """{"tasks":[{"key":"a","title":"A","dependsOn":["b"]},{"key":"b","title":"B","dependsOn":["a"]}]}""";
        var (_, demand) = await h.CreateDemandAsync(_origin, autoMode: true);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));

        Assert.True(await OrchestratorHarness.UntilAsync(() => h.WithDb(async db =>
            (await db.Demands.AsNoTracking().SingleAsync(d => d.Id == demand.Id)).Status == DemandStatus.Failed)));
        Assert.Equal(2, h.Adapter.Count(AgentRole.ScrumMaster));

        h.Adapter.PlanJson = """{"tasks":[{"key":"a","title":"Tarefa A","dependsOn":[]}]}""";
        Assert.Equal(ActionOutcome.Ok, await h.WithActions(a => a.StartDemandAsync(demand.Id)));
        Assert.True(await h.UntilItems(demand.Id, i => i.Count == 1 && i[0].State == WorkItemState.Done));
    }

    [PosixFact]
    public async Task Restart_in_the_middle_resumes_from_the_last_state()
    {
        var cs = pg.NewConnectionString();
        Guid demandId;

        // 1ª vida da API: o Dev trava; derrubamos o processo no meio.
        var first = await StartAsync(cs);
        first.Adapter.RawShell[AgentRole.Dev] = "exec sleep 120";
        var (_, demand) = await first.CreateDemandAsync(_origin, autoMode: true);
        demandId = demand.Id;
        await first.WithActions(a => a.StartDemandAsync(demandId));
        Assert.True(await OrchestratorHarness.UntilAsync(() => first.WithDb(async db =>
            await db.AgentRuns.AnyAsync(r => r.Role == AgentRole.Dev && r.Status == AgentRunStatus.Running))));
        await first.DisposeAsync();
        _harnesses.Remove(first);

        var interrupted = await first.WithDbAfterDispose(cs);
        Assert.Equal(WorkItemState.InDevelopment, interrupted.State);   // estado persistido antes do restart

        // uma execução que "ficou Running" porque a API morreu sem fechá-la
        await using (var db = await Db.CreateAsync(cs))
        {
            db.AgentRuns.Add(new AgentRun { Role = AgentRole.Dev, WorkItemId = interrupted.Id, Status = AgentRunStatus.Running, Prompt = "p" });
            await db.SaveChangesAsync();
        }

        // 2ª vida: retoma do estado salvo, sem ninguém pedir
        var second = await StartAsync(cs);
        Assert.True(await second.UntilItems(demandId, i => i.Count == 1 && i[0].State == WorkItemState.Done));
        Assert.True(await second.WithDb(db => db.AgentRuns.AnyAsync(r => r.Output == "Interrompida por reinício da API.")));
        Assert.False(await second.WithDb(db => db.AgentRuns.AnyAsync(r => r.Status == AgentRunStatus.Running)));
        Assert.Equal(0, second.Adapter.Count(AgentRole.ScrumMaster));   // não refez o refinamento
    }

    [PosixFact]
    public async Task Cancel_kills_the_agent_and_removes_worktree_and_branch()
    {
        var h = await StartAsync();
        h.Adapter.RawShell[AgentRole.Dev] = "exec sleep 120";
        var (project, demand) = await h.CreateDemandAsync(_origin, autoMode: true);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));
        Assert.True(await OrchestratorHarness.UntilAsync(() => h.WithDb(db =>
            db.AgentRuns.AnyAsync(r => r.Role == AgentRole.Dev && r.Status == AgentRunStatus.Running))));
        var item = await Item(h, demand.Id);
        var repo = Path.Combine(Repos, project.Id.ToString(), "repo");
        var worktree = Path.Combine(Repos, project.Id.ToString(), "worktrees", item.Id.ToString());
        Assert.True(Directory.Exists(worktree));

        Assert.Equal(ActionOutcome.Ok, await h.WithActions(a => a.CancelAsync(item.Id)));

        Assert.Equal(WorkItemState.Cancelled, (await Item(h, demand.Id)).State);
        Assert.False(Directory.Exists(worktree));
        Assert.False(await GitOrigin.HasBranchAsync(repo, $"agentforge/task-{item.Id}"));
        Assert.True(await OrchestratorHarness.UntilAsync(() => h.WithDb(db =>
            db.AgentRuns.AnyAsync(r => r.Role == AgentRole.Dev && r.Status == AgentRunStatus.Cancelled))));
        Assert.Equal(ActionOutcome.InvalidState, await h.WithActions(a => a.CancelAsync(item.Id)));
    }

    [PosixFact]
    public async Task With_concurrency_above_one_tech_lead_and_security_review_in_parallel()
    {
        var h = await StartAsync(concurrency: 2);
        var (_, demand) = await h.CreateDemandAsync(_origin, autoMode: true);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));

        Assert.True(await h.UntilItems(demand.Id, i => i.Count == 1 && i[0].State == WorkItemState.Done));
        Assert.Equal(1, h.Adapter.Count(AgentRole.TechLead));
        Assert.Equal(1, h.Adapter.Count(AgentRole.Security));
    }

    [PosixFact]
    public async Task Security_findings_in_parallel_mode_are_combined_for_the_dev()
    {
        var h = await StartAsync(concurrency: 2);
        h.Adapter.Enqueue(AgentRole.TechLead, """{"verdict":"changes_requested","findings":[{"message":"ajuste de estilo"}]}""");
        h.Adapter.Enqueue(AgentRole.Security, """{"verdict":"changes_requested","findings":[{"severity":"critical","message":"SQL injection no filtro"}]}""");
        var (_, demand) = await h.CreateDemandAsync(_origin, autoMode: true);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));

        Assert.True(await h.UntilItems(demand.Id, i => i.Count == 1 && i[0].State == WorkItemState.Done));

        var secondDevPrompt = h.Adapter.Requests.Where(r => r.Role == AgentRole.Dev).Select(r => r.Prompt).ElementAt(1);
        Assert.Contains("ajuste de estilo", secondDevPrompt);
        Assert.Contains("SQL injection no filtro", secondDevPrompt);
        Assert.Equal(1, (await Item(h, demand.Id)).ReworkCount);   // as duas revisões juntas contam como 1 retorno
    }

    [PosixFact]
    public async Task Dependent_task_waits_for_its_dependency_even_after_plan_approval()
    {
        var h = await StartAsync(concurrency: 2);   // com 1 trabalhador, B nem seria olhado enquanto A roda
        h.Adapter.PlanJson = """{"tasks":[{"key":"a","title":"Tarefa A","dependsOn":[]},{"key":"b","title":"Tarefa B","dependsOn":["a"]}]}""";
        h.Adapter.RawShell[AgentRole.Dev] = "exec sleep 120";   // A fica em desenvolvimento
        var (_, demand) = await h.CreateDemandAsync(_origin, autoMode: true);
        await h.WithActions(a => a.StartDemandAsync(demand.Id));

        Assert.True(await h.UntilItems(demand.Id, i => i.Count == 2 && i.Single(x => x.Title == "Tarefa A").State == WorkItemState.InDevelopment));
        await Task.Delay(500);

        Assert.Equal(WorkItemState.Todo, (await Item(h, demand.Id, "Tarefa B")).State);
    }
}

internal static class OrchestratorHarnessExtensions
{
    /// <summary>Lê a tarefa do banco depois de a harness antiga ter sido encerrada.</summary>
    public static async Task<WorkItem> WithDbAfterDispose(this OrchestratorHarness _, string cs)
    {
        await using var db = await Db.CreateAsync(cs);
        return await db.WorkItems.AsNoTracking().SingleAsync();
    }
}
