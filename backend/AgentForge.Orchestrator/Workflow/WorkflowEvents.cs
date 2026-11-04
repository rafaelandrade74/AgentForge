using AgentForge.Domain;

namespace AgentForge.Orchestrator.Workflow;

public record WorkItemChanged(
    Guid ProjectId, Guid DemandId, Guid WorkItemId, WorkItemState State, int ReworkCount,
    string? BlockedReason, string? BranchName, DateTime At, string? WaitingReason = null);

public record DemandChanged(Guid ProjectId, Guid DemandId, DemandStatus Status, DateTime At);

/// <summary>Mudanças de estado de tarefas e demandas, para o tempo real (a API troca a implementação por SignalR).</summary>
public interface IWorkflowEventPublisher
{
    Task WorkItemChangedAsync(WorkItemChanged evt);
    Task DemandChangedAsync(DemandChanged evt);
}

public class NullWorkflowEventPublisher : IWorkflowEventPublisher
{
    public Task WorkItemChangedAsync(WorkItemChanged evt) => Task.CompletedTask;
    public Task DemandChangedAsync(DemandChanged evt) => Task.CompletedTask;
}

public static class WorkflowEventExtensions
{
    public static Task PublishChangeAsync(this IWorkflowEventPublisher p, Guid projectId, WorkItem item) =>
        p.WorkItemChangedAsync(new WorkItemChanged(projectId, item.DemandId, item.Id, item.State, item.ReworkCount,
            item.BlockedReason, item.BranchName, DateTime.UtcNow, item.WaitingReason));

    public static Task PublishChangeAsync(this IWorkflowEventPublisher p, Demand demand) =>
        p.DemandChangedAsync(new DemandChanged(demand.ProjectId, demand.Id, demand.Status, DateTime.UtcNow));
}
