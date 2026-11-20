using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AgentForge.Orchestrator.Workflow;

/// <summary>Refinamento: o Scrum Master lê o repositório e quebra a demanda em WorkItems (em Backlog).</summary>
public class DemandProcessor(
    AppDbContext db,
    IWorkspaceManager workspaces,
    AgentExecutor executor,
    IWorkQueue queue,
    IWorkflowEventPublisher events,
    ILogger<DemandProcessor> logger)
{
    public async Task RefineAsync(Guid demandId, CancellationToken ct)
    {
        var demand = await db.Demands.FirstOrDefaultAsync(d => d.Id == demandId, ct);
        if (demand is null || demand.Status != DemandStatus.Refining) return;

        var isGroup = demand.ProjectGroupId is not null;
        List<Project> groupProjects = [];
        Project? project = null;

        if (isGroup)
        {
            groupProjects = await db.Projects.Where(p => p.ProjectGroupId == demand.ProjectGroupId).OrderBy(p => p.Name).ToListAsync(ct);
            if (groupProjects.Count == 0) { demand.Status = DemandStatus.Failed; await db.SaveChangesAsync(CancellationToken.None); await events.PublishChangeAsync(demand); return; }
            project = groupProjects[0];
        }
        else
        {
            project = await db.Projects.FirstAsync(p => p.Id == demand.ProjectId, ct);
        }

        var repo = await workspaces.EnsureRepositoryAsync(project, ct);
        var prompt = isGroup ? PromptBuilder.ScrumMasterGroup(demand, groupProjects) : PromptBuilder.ScrumMaster(demand, project);
        var result = await executor.RunAsync(AgentRole.ScrumMaster, prompt, repo,
            project.Id, demand.Id, null, json => Task.FromResult(OutputParsers.ParsePlan(json, isGroup ? groupProjects : null)), ct);

        if (result.Outcome == ExecOutcome.Cancelled) return;
        result.ThrowIfUnavailable();
        if (result.Outcome == ExecOutcome.Failed)
        {
            logger.LogWarning("Refinamento da demanda {Id} falhou: {Error}", demandId, result.Error);
            demand.Status = DemandStatus.Failed;
            await db.SaveChangesAsync(CancellationToken.None);
            await events.PublishChangeAsync(demand);
            return;
        }

        var projectsByName = isGroup ? groupProjects.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase) : null;

        var byKey = result.Value!.ToDictionary(t => t.Key, t =>
        {
            var wi = new WorkItem
            {
                DemandId = demand.Id, Title = t.Title.Trim(), Description = t.Description,
                AcceptanceCriteria = t.AcceptanceCriteria, Estimate = t.Estimate, State = WorkItemState.Backlog,
            };
            if (isGroup && t.Project is not null && projectsByName!.TryGetValue(t.Project, out var targetProject))
                wi.ProjectId = targetProject.Id;
            return wi;
        });
        foreach (var t in result.Value!)
            byKey[t.Key].Dependencies = t.DependsOn.Select(k => byKey[k]).ToList();

        db.WorkItems.AddRange(byKey.Values);
        demand.Status = DemandStatus.InProgress;

        if (isGroup)
        {
            var projectsWithTasks = byKey.Values.Where(w => w.ProjectId is not null).Select(w => w.ProjectId!.Value).Distinct();
            foreach (var pid in projectsWithTasks)
            {
                var proj = groupProjects.First(p => p.Id == pid);
                await workspaces.EnsureDemandBranchAsync(proj, demand.Id, CancellationToken.None);
            }
        }
        else
        {
            demand.BranchName = await workspaces.EnsureDemandBranchAsync(project, demand.Id, CancellationToken.None);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        await events.PublishChangeAsync(demand);

        var eventProjectId = isGroup ? groupProjects[0].Id : project.Id;
        foreach (var item in byKey.Values)
        {
            await events.PublishChangeAsync(item.ProjectId ?? eventProjectId, item);
            queue.EnqueueWorkItem(item.Id);
        }
    }
}
