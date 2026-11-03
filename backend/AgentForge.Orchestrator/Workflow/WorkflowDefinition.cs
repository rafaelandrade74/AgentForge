using AgentForge.Domain;

namespace AgentForge.Orchestrator.Workflow;

public enum WorkflowEvent
{
    HumanApproved,      // aprova o plano (Backlog) ou a entrega (AwaitingApproval)
    HumanRejected,
    Started,            // dependências em Done
    DevDone,
    TechLeadApproved,   // revisões em sequência
    SecurityApproved,
    ReviewsApproved,    // revisões em paralelo: as duas aprovaram
    ChangesRequested,
    QaPassed,
    QaFailed,
    ReworkLimitReached,
    AgentFailed,
    Retry,
    Cancel,
}

/// <summary>
/// Tabela de transições válidas do WorkItem. Para acrescentar uma etapa, basta incluir linhas aqui
/// (e o passo correspondente no WorkItemProcessor).
/// </summary>
public static class WorkflowDefinition
{
    /// <summary>No terceiro retorno ao Dev a tarefa vai para Blocked.</summary>
    public const int MaxRework = 3;

    private static readonly Dictionary<(WorkItemState, WorkflowEvent), WorkItemState> Table = Build();

    private static Dictionary<(WorkItemState, WorkflowEvent), WorkItemState> Build()
    {
        var t = new Dictionary<(WorkItemState, WorkflowEvent), WorkItemState>
        {
            [(WorkItemState.Backlog, WorkflowEvent.HumanApproved)] = WorkItemState.Todo,
            [(WorkItemState.Todo, WorkflowEvent.Started)] = WorkItemState.InDevelopment,

            [(WorkItemState.InDevelopment, WorkflowEvent.DevDone)] = WorkItemState.TechLeadReview,
            [(WorkItemState.InDevelopment, WorkflowEvent.AgentFailed)] = WorkItemState.Blocked,

            [(WorkItemState.TechLeadReview, WorkflowEvent.TechLeadApproved)] = WorkItemState.SecurityReview,
            [(WorkItemState.TechLeadReview, WorkflowEvent.ReviewsApproved)] = WorkItemState.Qa,
            [(WorkItemState.TechLeadReview, WorkflowEvent.ChangesRequested)] = WorkItemState.InDevelopment,
            [(WorkItemState.TechLeadReview, WorkflowEvent.ReworkLimitReached)] = WorkItemState.Blocked,
            [(WorkItemState.TechLeadReview, WorkflowEvent.AgentFailed)] = WorkItemState.Blocked,

            [(WorkItemState.SecurityReview, WorkflowEvent.SecurityApproved)] = WorkItemState.Qa,
            [(WorkItemState.SecurityReview, WorkflowEvent.ChangesRequested)] = WorkItemState.InDevelopment,
            [(WorkItemState.SecurityReview, WorkflowEvent.ReworkLimitReached)] = WorkItemState.Blocked,
            [(WorkItemState.SecurityReview, WorkflowEvent.AgentFailed)] = WorkItemState.Blocked,

            [(WorkItemState.Qa, WorkflowEvent.QaPassed)] = WorkItemState.AwaitingApproval,
            [(WorkItemState.Qa, WorkflowEvent.QaFailed)] = WorkItemState.InDevelopment,
            [(WorkItemState.Qa, WorkflowEvent.ReworkLimitReached)] = WorkItemState.Blocked,
            [(WorkItemState.Qa, WorkflowEvent.AgentFailed)] = WorkItemState.Blocked,

            [(WorkItemState.AwaitingApproval, WorkflowEvent.HumanApproved)] = WorkItemState.Done,
            [(WorkItemState.AwaitingApproval, WorkflowEvent.HumanRejected)] = WorkItemState.InDevelopment,

            [(WorkItemState.Blocked, WorkflowEvent.Retry)] = WorkItemState.InDevelopment,
        };

        foreach (var state in Enum.GetValues<WorkItemState>().Where(s => !IsTerminal(s)))
            t[(state, WorkflowEvent.Cancel)] = WorkItemState.Cancelled;
        return t;
    }

    public static WorkItemState? Next(WorkItemState state, WorkflowEvent evt) =>
        Table.TryGetValue((state, evt), out var next) ? next : null;

    public static bool IsTerminal(WorkItemState s) => s is WorkItemState.Done or WorkItemState.Cancelled;

    /// <summary>Estados que o orquestrador precisa retomar depois de um restart.</summary>
    public static readonly WorkItemState[] Resumable =
    [
        WorkItemState.Backlog, WorkItemState.Todo, WorkItemState.InDevelopment, WorkItemState.TechLeadReview,
        WorkItemState.SecurityReview, WorkItemState.Qa, WorkItemState.AwaitingApproval,
    ];
}
