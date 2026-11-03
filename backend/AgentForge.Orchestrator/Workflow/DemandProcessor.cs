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
    ILogger<DemandProcessor> logger)
{
    public async Task RefineAsync(Guid demandId, CancellationToken ct)
    {
        var demand = await db.Demands.FirstOrDefaultAsync(d => d.Id == demandId, ct);
        if (demand is null || demand.Status != DemandStatus.Refining) return;
        var project = await db.Projects.FirstAsync(p => p.Id == demand.ProjectId, ct);

        var repo = await workspaces.EnsureRepositoryAsync(project, ct);
        var result = await executor.RunAsync(AgentRole.ScrumMaster, PromptBuilder.ScrumMaster(demand, project), repo,
            demand.Id, null, json => Task.FromResult(OutputParsers.ParsePlan(json)), ct);

        if (result.Outcome == ExecOutcome.Cancelled) return;   // restart/cancelamento: continua Refining e é retomada
        if (result.Outcome == ExecOutcome.Failed)
        {
            logger.LogWarning("Refinamento da demanda {Id} falhou: {Error}", demandId, result.Error);
            demand.Status = DemandStatus.Failed;
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        var byKey = result.Value!.ToDictionary(t => t.Key, t => new WorkItem
        {
            DemandId = demand.Id, Title = t.Title.Trim(), Description = t.Description,
            AcceptanceCriteria = t.AcceptanceCriteria, Estimate = t.Estimate, State = WorkItemState.Backlog,
        });
        foreach (var t in result.Value!)
            byKey[t.Key].Dependencies = t.DependsOn.Select(k => byKey[k]).ToList();

        db.WorkItems.AddRange(byKey.Values);
        demand.Status = DemandStatus.InProgress;
        await db.SaveChangesAsync(CancellationToken.None);

        foreach (var item in byKey.Values) queue.EnqueueWorkItem(item.Id);
    }
}
