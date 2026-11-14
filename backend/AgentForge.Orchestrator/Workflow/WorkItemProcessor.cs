using System.Text.RegularExpressions;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.Orchestrator.Workflow;

/// <summary>
/// Executa UM passo do WorkItem conforme o estado persistido e grava a transição. O estado é salvo a cada
/// transição; por isso, depois de um restart, repetir o passo do estado atual retoma o fluxo.
/// </summary>
public partial class WorkItemProcessor(
    AppDbContext db,
    IWorkspaceManager workspaces,
    AgentExecutor executor,
    IWorkQueue queue,
    IWorkflowEventPublisher events,
    IEnvironmentLaunchers environments,
    IOptions<OrchestratorOptions> options,
    ILogger<WorkItemProcessor> logger,
    IBranchPublisher? publisher = null)
{
    private const int MaxDiffChars = 100_000;

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex HashPattern();

    // ------------------------------------------------------------------ passos automáticos

    public async Task<StepResult> StepAsync(Guid id, CancellationToken ct)
    {
        var item = await db.WorkItems.Include(w => w.Dependencies).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (item is null) return StepResult.Stop;
        var demand = await db.Demands.FirstAsync(d => d.Id == item.DemandId, ct);
        var project = await db.Projects.FirstAsync(p => p.Id == demand.ProjectId, ct);

        return item.State switch
        {
            WorkItemState.Backlog or WorkItemState.AwaitingApproval when project.AutoMode =>
                Continue(await TransitionAsync(item, project, WorkflowEvent.HumanApproved, ct: ct)),
            WorkItemState.Todo when item.Dependencies.All(d => d.State == WorkItemState.Done) =>
                Continue(await TransitionAsync(item, project, WorkflowEvent.Started, ct: ct)),
            WorkItemState.InDevelopment => await DevStepAsync(item, demand, project, ct),
            WorkItemState.TechLeadReview => await ReviewStepAsync(item, demand, project, ct),
            WorkItemState.SecurityReview => await SecurityStepAsync(item, demand, project, ct),
            WorkItemState.Qa => await QaStepAsync(item, demand, project, ct),
            _ => StepResult.Stop,
        };
    }

    private static StepResult Continue(bool advanced) => advanced ? StepResult.Continue : StepResult.Stop;

    /// <summary>
    /// Worktree da tarefa. Na primeira vez ele nasce do branch base e combina o trabalho das dependências; o ponto de
    /// partida (BaseCommit) é guardado para as revisões olharem só o que a tarefa mudou.
    /// </summary>
    private async Task<Workspace> OpenWorkspaceAsync(WorkItem item, Project project, CancellationToken ct)
    {
        var ws = await workspaces.CreateWorktreeAsync(project, item.Id, item.DemandId, ct, item.BaseCommit);
        if (item.BaseCommit != ws.BaseCommit)
        {
            item.BaseCommit = ws.BaseCommit;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return ws;
    }

    private async Task<StepResult> DevStepAsync(WorkItem item, Demand demand, Project project, CancellationToken ct)
    {
        Workspace ws;
        try
        {
            ws = await OpenWorkspaceAsync(item, project, ct);
        }
        catch (WorkspaceException ex)
        {
            return Continue(await BlockAsync(item, project, "Workspace", ex.Message, ct));
        }
        var open = await db.ReviewFindings.Where(f => f.WorkItemId == item.Id && !f.Resolved).OrderBy(f => f.Role).ToListAsync(ct);
        var depTitles = item.Dependencies.Select(d => d.Title);
        var prompt = PromptBuilder.Dev(item, project, depTitles, open);

        var result = await executor.RunAsync(AgentRole.Dev, prompt, ws.Path, project.Id, demand.Id, item.Id,
            json => ParseDevAsync(json, project, ws.Path, ws.BaseCommit, ct), ct);
        if (result.Outcome == ExecOutcome.Cancelled) return StepResult.Stop;
        result.ThrowIfUnavailable();
        if (result.Outcome == ExecOutcome.Failed) return Continue(await BlockAsync(item, project, "Dev", result.Error, ct));

        foreach (var f in open) f.Resolved = true;   // o Dev tratou os apontamentos; a próxima revisão gera novos se faltar algo
        return Continue(await TransitionAsync(item, project, WorkflowEvent.DevDone,
            w => w.BranchName = ws.Branch, ct: ct));
    }

    /// <summary>O ambiente alternativo do papel no projeto (Tech Lead/Security numa máquina leve); null = o do projeto.</summary>
    private async Task<RoleEnvironment?> RoleEnvironmentAsync(Project project, AgentRole role, CancellationToken ct) =>
        await db.ProjectRoleEnvironments.AsNoTracking().Where(r => r.ProjectId == project.Id && r.Role == role)
            .Select(r => new RoleEnvironment(r.EnvironmentId)).FirstOrDefaultAsync(ct);

    private async Task<StepResult> ReviewStepAsync(WorkItem item, Demand demand, Project project, CancellationToken ct)
    {
        var ws = await OpenWorkspaceAsync(item, project, ct);
        var diff = await DiffAsync(project, ws.Path, ws.BaseCommit, ct);
        var parallel = options.Value.MaxConcurrency > 1;
        var tlEnv = await RoleEnvironmentAsync(project, AgentRole.TechLead, ct);
        var secEnv = await RoleEnvironmentAsync(project, AgentRole.Security, ct);

        var tlTask = executor.RunAsync(AgentRole.TechLead, PromptBuilder.TechLead(item, project, diff, repoAvailable: tlEnv is null), ws.Path,
            project.Id, demand.Id, item.Id, json => Task.FromResult(OutputParsers.ParseReview(json)), ct, tlEnv);
        var secTask = parallel
            ? executor.RunAsync(AgentRole.Security, PromptBuilder.Security(item, project, diff, repoAvailable: secEnv is null), ws.Path,
                project.Id, demand.Id, item.Id, json => Task.FromResult(OutputParsers.ParseReview(json)), ct, secEnv)
            : null;

        var reviews = new List<(AgentRole Role, ExecResult<ReviewReport> Result)> { (AgentRole.TechLead, await tlTask) };
        if (secTask is not null) reviews.Add((AgentRole.Security, await secTask));

        return await ConcludeReviewsAsync(item, project, reviews, parallel ? WorkflowEvent.ReviewsApproved : WorkflowEvent.TechLeadApproved, ct);
    }

    private async Task<StepResult> SecurityStepAsync(WorkItem item, Demand demand, Project project, CancellationToken ct)
    {
        var ws = await OpenWorkspaceAsync(item, project, ct);
        var diff = await DiffAsync(project, ws.Path, ws.BaseCommit, ct);
        var secEnv = await RoleEnvironmentAsync(project, AgentRole.Security, ct);
        var result = await executor.RunAsync(AgentRole.Security, PromptBuilder.Security(item, project, diff, repoAvailable: secEnv is null), ws.Path,
            project.Id, demand.Id, item.Id, json => Task.FromResult(OutputParsers.ParseReview(json)), ct, secEnv);
        return await ConcludeReviewsAsync(item, project, [(AgentRole.Security, result)], WorkflowEvent.SecurityApproved, ct);
    }

    private async Task<StepResult> ConcludeReviewsAsync(WorkItem item, Project project,
        List<(AgentRole Role, ExecResult<ReviewReport> Result)> reviews, WorkflowEvent approvedEvent, CancellationToken ct)
    {
        if (reviews.Any(r => r.Result.Outcome == ExecOutcome.Cancelled)) return StepResult.Stop;
        foreach (var r in reviews) r.Result.ThrowIfUnavailable();
        var failed = reviews.FirstOrDefault(r => r.Result.Outcome == ExecOutcome.Failed);
        if (failed.Result is not null) return Continue(await BlockAsync(item, project, failed.Role.ToString(), failed.Result.Error, ct));

        var findings = reviews
            .Where(r => !r.Result.Value!.Approved)
            .SelectMany(r => r.Result.Value!.Findings.Select(f => new ReviewFinding
            {
                WorkItemId = item.Id, AgentRunId = r.Result.RunId, Role = r.Role,
                Severity = f.Severity, File = f.File, Line = f.Line, Message = f.Message,
            })).ToList();

        return findings.Count == 0
            ? Continue(await TransitionAsync(item, project, approvedEvent, ct: ct))
            : await ReworkAsync(item, project, WorkflowEvent.ChangesRequested, findings, ct);
    }

    private async Task<StepResult> QaStepAsync(WorkItem item, Demand demand, Project project, CancellationToken ct)
    {
        var ws = await OpenWorkspaceAsync(item, project, ct);
        var result = await executor.RunAsync(AgentRole.Qa, PromptBuilder.Qa(item, project), ws.Path, project.Id, demand.Id, item.Id,
            json => Task.FromResult(OutputParsers.ParseQa(json)), ct);
        if (result.Outcome == ExecOutcome.Cancelled) return StepResult.Stop;
        result.ThrowIfUnavailable();
        if (result.Outcome == ExecOutcome.Failed) return Continue(await BlockAsync(item, project, "QA", result.Error, ct));

        if (result.Value!.Passed) return Continue(await TransitionAsync(item, project, WorkflowEvent.QaPassed, ct: ct));

        var findings = result.Value.Criteria.Where(c => !c.Passed).Select(c => new ReviewFinding
        {
            WorkItemId = item.Id, AgentRunId = result.RunId, Role = AgentRole.Qa, Severity = FindingSeverity.Medium,
            Message = string.IsNullOrWhiteSpace(c.Evidence) ? c.Criterion : $"{c.Criterion} — {c.Evidence}",
        });
        return await ReworkAsync(item, project, WorkflowEvent.QaFailed, findings, ct);
    }

    /// <summary>Devolve ao Dev com os apontamentos; no terceiro retorno a tarefa vai para Blocked.</summary>
    private async Task<StepResult> ReworkAsync(WorkItem item, Project project, WorkflowEvent evt,
        IEnumerable<ReviewFinding> findings, CancellationToken ct)
    {
        db.ReviewFindings.AddRange(findings);
        var count = item.ReworkCount + 1;
        return count >= WorkflowDefinition.MaxRework
            ? Continue(await TransitionAsync(item, project, WorkflowEvent.ReworkLimitReached, w => w.ReworkCount = count,
                $"A tarefa voltou ao Dev {WorkflowDefinition.MaxRework} vezes; aguarda decisão humana.", ct))
            : Continue(await TransitionAsync(item, project, evt, w => w.ReworkCount = count, ct: ct));
    }

    private Task<bool> BlockAsync(WorkItem item, Project project, string who, string? error, CancellationToken ct) =>
        TransitionAsync(item, project, WorkflowEvent.AgentFailed, reason: $"{who}: {error ?? "falha desconhecida"}", ct: ct);

    // ------------------------------------------------------------------ validações com git

    private async Task<Parsed<DevReport>> ParseDevAsync(string? json, Project project, string workspacePath, string baseCommit, CancellationToken ct)
    {
        var launcher = (await environments.ForProjectAsync(project, ct)).Launcher;
        var parsed = OutputParsers.ParseDev(json);
        if (!parsed.Ok) return parsed;

        var hash = parsed.Value!.Commit;
        if (!HashPattern().IsMatch(hash)) return Parsed<DevReport>.Fail($"\"{hash}\" não é um hash de commit.");
        try
        {
            await GitCli.RunAsync(launcher, workspacePath, ["cat-file", "-e", hash + "^{commit}"], ct);
            await GitCli.RunAsync(launcher, workspacePath, ["merge-base", "--is-ancestor", hash, "HEAD"], ct);
            var ahead = await GitCli.RunAsync(launcher, workspacePath, ["rev-list", "--count", $"{baseCommit}..HEAD"], ct);
            if (ahead.Trim() == "0") return Parsed<DevReport>.Fail("O branch não tem nenhum commit novo da tarefa (depois do ponto de partida).");
        }
        catch (GitException)
        {
            return Parsed<DevReport>.Fail($"O commit {hash} não existe no branch atual do worktree.");
        }
        return parsed;
    }

    /// <summary>O que a tarefa mudou: do ponto de partida (base + dependências) até o HEAD do worktree.</summary>
    private async Task<string> DiffAsync(Project project, string workspacePath, string baseCommit, CancellationToken ct)
    {
        var launcher = (await environments.ForProjectAsync(project, ct)).Launcher;
        try
        {
            var diff = await GitCli.RunAsync(launcher, workspacePath, ["diff", baseCommit, "HEAD"], ct);
            return diff.Length <= MaxDiffChars ? diff : diff[..MaxDiffChars] + "\n… (diff truncado; use git diff para ver o resto)";
        }
        catch (GitException ex)
        {
            return $"(não foi possível gerar o diff: {ex.Message})";
        }
    }

    // ------------------------------------------------------------------ transições e ações humanas

    /// <summary>
    /// Aplica um evento: relê o estado do banco (se a tarefa foi cancelada nesse meio tempo, não faz nada),
    /// valida na WorkflowDefinition, grava e executa os efeitos da chegada ao novo estado.
    /// </summary>
    private async Task<bool> TransitionAsync(WorkItem item, Project project, WorkflowEvent evt,
        Action<WorkItem>? mutate = null, string? reason = null, CancellationToken ct = default)
    {
        var expected = item.State;
        await db.Entry(item).ReloadAsync(ct);
        if (item.State != expected) return false;

        var next = WorkflowDefinition.Next(expected, evt)
                   ?? throw new InvalidOperationException($"Transição inválida: {expected} + {evt}.");
        if (evt == WorkflowEvent.Retry)
            next = WorkflowDefinition.RetryTarget(item.BlockedFromState);
        mutate?.Invoke(item);
        item.State = next;
        item.BlockedReason = next == WorkItemState.Blocked ? reason : null;
        if (next == WorkItemState.Blocked)
            item.BlockedFromState = expected;
        else
            item.BlockedFromState = null;
        item.WaitingReason = null;
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("WorkItem {Id}: {From} --{Event}--> {To}", item.Id, expected, evt, next);

        await events.PublishChangeAsync(project.Id, item);
        await OnEnteredAsync(item, project, next);
        return true;
    }

    private async Task OnEnteredAsync(WorkItem item, Project project, WorkItemState state)
    {
        switch (state)
        {
            case WorkItemState.Done:
                await workspaces.RemoveWorktreeAsync(project, item.Id, deleteBranch: false, CancellationToken.None);
                try
                {
                    await workspaces.MergeTaskIntoDemandAsync(project, item.DemandId, item.Id, CancellationToken.None);
                }
                catch (WorkspaceException ex)
                {
                    logger.LogWarning(ex, "Merge da tarefa {TaskId} no branch da demanda falhou", item.Id);
                }
                var dependents = await db.WorkItems
                    .Where(w => w.State == WorkItemState.Todo && w.Dependencies.Any(d => d.Id == item.Id))
                    .Select(w => w.Id).ToListAsync();
                foreach (var id in dependents) queue.EnqueueWorkItem(id);
                await CompleteDemandIfFinishedAsync(item.DemandId);
                break;
            case WorkItemState.Cancelled:
                await queue.CancelRunningAsync(item.Id);
                await workspaces.RemoveWorktreeAsync(project, item.Id, deleteBranch: true, CancellationToken.None);
                await ReassessDependentsAsync(item, project);
                await CompleteDemandIfFinishedAsync(item.DemandId);
                break;
        }
    }

    /// <summary>
    /// Quando uma tarefa é cancelada, o Scrum Master reavalia as dependentes: remove a dependência (ajustando
    /// descrição/critérios) ou bloqueia para decisão humana. Se o Scrum Master falhar, as dependentes vão para Blocked.
    /// </summary>
    private async Task ReassessDependentsAsync(WorkItem cancelled, Project project)
    {
        var dependents = await db.WorkItems
            .Include(w => w.Dependencies)
            .Where(w => w.State != WorkItemState.Done && w.State != WorkItemState.Cancelled && w.Dependencies.Any(d => d.Id == cancelled.Id))   // sem chamar método C# dentro da consulta: o EF não traduz
            .ToListAsync();
        if (dependents.Count == 0) return;

        var repo = await workspaces.EnsureRepositoryAsync(project, CancellationToken.None);
        var prompt = PromptBuilder.ScrumMasterReassess(cancelled, dependents, project);
        var validIds = dependents.Select(d => d.Id).ToHashSet();

        var result = await executor.RunAsync(AgentRole.ScrumMaster, prompt, repo,
            project.Id, null, null,
            json => Task.FromResult(OutputParsers.ParseReassessment(json, validIds)),
            CancellationToken.None);

        if (result.Outcome == ExecOutcome.Ok && result.Value is not null)
        {
            var actionsById = result.Value.Actions.ToDictionary(a => a.WorkItemId);
            foreach (var dep in dependents)
            {
                if (actionsById.TryGetValue(dep.Id, out var action) && action.Action == "remove_dependency")
                {
                    dep.Dependencies.RemoveAll(d => d.Id == cancelled.Id);
                    if (!string.IsNullOrWhiteSpace(action.NewDescription))
                        dep.Description = action.NewDescription;
                    if (!string.IsNullOrWhiteSpace(action.NewAcceptanceCriteria))
                        dep.AcceptanceCriteria = action.NewAcceptanceCriteria;
                    await db.SaveChangesAsync(CancellationToken.None);
                    await events.PublishChangeAsync(project.Id, dep);
                    queue.EnqueueWorkItem(dep.Id);
                }
                else
                {
                    await BlockDependentAsync(dep, project, cancelled);
                }
            }
        }
        else
        {
            logger.LogWarning("Scrum Master falhou ao reavaliar dependentes da tarefa cancelada {Id}: {Error}", cancelled.Id, result.Error);
            foreach (var dep in dependents)
                await BlockDependentAsync(dep, project, cancelled);
        }
    }

    private async Task BlockDependentAsync(WorkItem dep, Project project, WorkItem cancelled)
    {
        if (WorkflowDefinition.IsTerminal(dep.State)) return;
        var reason = $"Dependência cancelada: \"{cancelled.Title}\" ({cancelled.Id}) — aguarda decisão humana.";
        if (WorkflowDefinition.Next(dep.State, WorkflowEvent.AgentFailed) is not null)
            await TransitionAsync(dep, project, WorkflowEvent.AgentFailed, reason: reason, ct: CancellationToken.None);
        else
            await TransitionAsync(dep, project, WorkflowEvent.Cancel, reason: reason, ct: CancellationToken.None);
    }

    private async Task CompleteDemandIfFinishedAsync(Guid demandId)
    {
        var states = await db.WorkItems.Where(w => w.DemandId == demandId).Select(w => w.State).ToListAsync();
        if (states.Count == 0 || states.Any(s => !WorkflowDefinition.IsTerminal(s)) || states.All(s => s == WorkItemState.Cancelled)) return;
        var updated = await db.Demands.Where(d => d.Id == demandId && d.Status == DemandStatus.InProgress)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DemandStatus.Done));
        if (updated > 0)
        {
            var demand = await db.Demands.FirstAsync(d => d.Id == demandId);
            await events.PublishChangeAsync(demand);
            await PublishDemandBranchAsync(demand);
        }
    }

    private async Task PublishDemandBranchAsync(Demand demand)
    {
        if (publisher is null) return;
        try
        {
            await publisher.PublishDemandAsync(demand.Id, CancellationToken.None);
            await db.Entry(demand).ReloadAsync(CancellationToken.None);
            await events.PublishChangeAsync(demand);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível publicar o branch da demanda {Id}", demand.Id);
        }
    }

    /// <summary>Evento vindo de uma pessoa (aprovar, rejeitar, retomar, cancelar).</summary>
    public async Task<ActionOutcome> ApplyHumanEventAsync(Guid id, WorkflowEvent evt, string? comment)
    {
        var item = await db.WorkItems.Include(w => w.Dependencies).FirstOrDefaultAsync(w => w.Id == id);
        if (item is null) return ActionOutcome.NotFound;
        if (WorkflowDefinition.Next(item.State, evt) is null) return ActionOutcome.InvalidState;
        var project = await db.Projects.FirstAsync(p => p.Id == db.Demands.First(d => d.Id == item.DemandId).ProjectId);

        if (evt == WorkflowEvent.HumanRejected && !string.IsNullOrWhiteSpace(comment))
            db.ReviewFindings.Add(new ReviewFinding
            {
                WorkItemId = id, Role = AgentRole.Human, Severity = FindingSeverity.High, Message = comment.Trim(),
            });

        // Retomar um Blocked é uma decisão humana: zera o contador de retrabalho.
        Action<WorkItem>? mutate = evt == WorkflowEvent.Retry ? w => w.ReworkCount = 0 : null;
        if (!await TransitionAsync(item, project, evt, mutate)) return ActionOutcome.InvalidState;

        queue.EnqueueWorkItem(id);
        return ActionOutcome.Ok;
    }

    /// <summary>Último recurso quando um passo lança exceção inesperada: não deixa o item em loop.</summary>
    public async Task FailAsync(Guid id, string message)
    {
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id);
        if (item is null || WorkflowDefinition.Next(item.State, WorkflowEvent.AgentFailed) is null) return;
        item.State = WorkItemState.Blocked;
        item.BlockedReason = "Erro interno: " + message;
        await db.SaveChangesAsync();
        var projectId = await db.Demands.Where(d => d.Id == item.DemandId).Select(d => d.ProjectId).FirstAsync();
        await events.PublishChangeAsync(projectId, item);
    }
}
