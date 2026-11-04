namespace AgentForge.Domain;

/// <summary>Human só aparece em ReviewFinding (comentário de uma rejeição); não é um agente.</summary>
public enum AgentRole { ScrumMaster, Dev, TechLead, Security, Qa, Human }

public enum DemandStatus { New, Refining, InProgress, Done, Cancelled, Failed }

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

public enum EnvironmentKind { HostAgent }
