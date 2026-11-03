namespace AgentForge.Domain;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string BaseBranch { get; set; } = "main";
    public string BuildCommand { get; set; } = "";
    public string TestCommand { get; set; } = "";
    /// <summary>Quando true, desliga as aprovações humanas do fluxo (modo automático).</summary>
    public bool AutoMode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Demand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DemandStatus Status { get; set; } = DemandStatus.New;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class WorkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DemandId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AcceptanceCriteria { get; set; } = "";
    public int Estimate { get; set; }
    public WorkItemState State { get; set; } = WorkItemState.Backlog;
    /// <summary>Quantas vezes a tarefa voltou ao Dev após uma revisão reprovada.</summary>
    public int ReworkCount { get; set; }
    public string? BranchName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<WorkItem> Dependencies { get; set; } = [];
}

public class AgentRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? DemandId { get; set; }
    public Guid? WorkItemId { get; set; }
    public AgentRole Role { get; set; }
    public string Prompt { get; set; } = "";
    public string Output { get; set; } = "";
    public decimal CostUsd { get; set; }
    public long DurationMs { get; set; }
    public AgentRunStatus Status { get; set; } = AgentRunStatus.Pending;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

public class ReviewFinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public Guid? AgentRunId { get; set; }
    public AgentRole Role { get; set; }
    public FindingSeverity Severity { get; set; } = FindingSeverity.Medium;
    public string? File { get; set; }
    public int? Line { get; set; }
    public string Message { get; set; } = "";
    public bool Resolved { get; set; }
}

public class AgentRoleConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AgentRole Role { get; set; }
    /// <summary>Nome do ICliAgentAdapter registrado no DI (ex.: "claude-code").</summary>
    public string AdapterName { get; set; } = "claude-code";
    public string SystemPrompt { get; set; } = "";
    /// <summary>Ferramentas permitidas, separadas por vírgula.</summary>
    public string AllowedTools { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxTurns { get; set; } = 20;
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TokenHash { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}
