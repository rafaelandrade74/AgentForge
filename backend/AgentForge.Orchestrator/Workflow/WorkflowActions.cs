using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Orchestrator.Workflow;

/// <summary>Ações de uma pessoa sobre o fluxo, usadas pelos endpoints da API.</summary>
public class WorkflowActions(AppDbContext db, WorkItemProcessor processor, IWorkQueue queue, IWorkflowEventPublisher events)
{
    /// <summary>Inicia (ou reinicia, se falhou) o refinamento da demanda pelo Scrum Master.</summary>
    public async Task<ActionOutcome> StartDemandAsync(Guid demandId)
    {
        var demand = await db.Demands.FirstOrDefaultAsync(d => d.Id == demandId);
        if (demand is null) return ActionOutcome.NotFound;
        if (demand.Status is not (DemandStatus.New or DemandStatus.Failed)) return ActionOutcome.InvalidState;
        demand.Status = DemandStatus.Refining;
        await db.SaveChangesAsync();
        await events.PublishChangeAsync(demand);
        queue.EnqueueDemand(demandId);
        return ActionOutcome.Ok;
    }

    /// <summary>Aprovação humana nº 1: libera todas as tarefas em Backlog da demanda.</summary>
    public async Task<ActionOutcome> ApprovePlanAsync(Guid demandId)
    {
        if (!await db.Demands.AnyAsync(d => d.Id == demandId)) return ActionOutcome.NotFound;
        var ids = await db.WorkItems.Where(w => w.DemandId == demandId && w.State == WorkItemState.Backlog).Select(w => w.Id).ToListAsync();
        if (ids.Count == 0) return ActionOutcome.InvalidState;
        foreach (var id in ids) await processor.ApplyHumanEventAsync(id, WorkflowEvent.HumanApproved, null);
        return ActionOutcome.Ok;
    }

    /// <summary>Aprova a tarefa: plano (Backlog → Todo) ou entrega (AwaitingApproval → Done).</summary>
    public Task<ActionOutcome> ApproveAsync(Guid id) => processor.ApplyHumanEventAsync(id, WorkflowEvent.HumanApproved, null);

    /// <summary>Rejeita a entrega: volta ao Dev com o comentário como apontamento.</summary>
    public Task<ActionOutcome> RejectAsync(Guid id, string? comment) => processor.ApplyHumanEventAsync(id, WorkflowEvent.HumanRejected, comment);

    /// <summary>Tira a tarefa de Blocked e devolve ao Dev (zera o contador de retrabalho).</summary>
    public Task<ActionOutcome> RetryAsync(Guid id) => processor.ApplyHumanEventAsync(id, WorkflowEvent.Retry, null);

    public Task<ActionOutcome> CancelAsync(Guid id) => processor.ApplyHumanEventAsync(id, WorkflowEvent.Cancel, null);
}
