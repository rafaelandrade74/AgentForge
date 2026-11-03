namespace AgentForge.Domain;

public enum AgentRole { ScrumMaster, Dev, TechLead, Security, Qa }

public enum DemandStatus { New, Refining, InProgress, Done, Cancelled }

/// <summary>Estados do WorkItem. As transições válidas ficam na WorkflowDefinition (Fase 5).</summary>
public enum WorkItemState
{
    Backlog,
    Todo,
    InDevelopment,
    TechLeadReview,
    SecurityReview,
    Qa,
    AwaitingApproval,
    Done,
    Blocked,
    Cancelled,
}

public enum AgentRunStatus { Pending, Running, Succeeded, Failed, Cancelled }

public enum FindingSeverity { Info, Low, Medium, High, Critical }
