using AgentForge.Domain;

namespace AgentForge.Api;

/// <param name="RoleEnvironments">Papel ("TechLead" | "Security") → ambiente onde ele roda em vez do ambiente do projeto. Omitido = não muda; vazio = limpa.</param>
/// <param name="PullRequestBaseBranch">Branch de destino dos PRs (ex.: develop). Omitido = não muda; vazio = a branch base do projeto.</param>
/// <param name="GitHubToken">Token para abrir PRs. Omitido = mantém o guardado; vazio = apaga. Nunca é devolvido.</param>
public record ProjectInput(string Name, string RepositoryUrl, string BaseBranch = "main",
    string BuildCommand = "", string TestCommand = "", bool AutoMode = false,
    Guid? EnvironmentId = null, Guid? StackProfileId = null, Dictionary<string, Guid?>? RoleEnvironments = null,
    string? PullRequestBaseBranch = null, string? GitHubToken = null);
public record ProjectDto(Guid Id, string Name, string RepositoryUrl, string BaseBranch,
    string BuildCommand, string TestCommand, bool AutoMode, DateTime CreatedAt, Guid? EnvironmentId = null, Guid? StackProfileId = null,
    Dictionary<string, Guid>? RoleEnvironments = null, string PullRequestBaseBranch = "", bool HasGitHubToken = false);

public record DemandInput(Guid ProjectId, string Title, string Description = "", DemandStatus Status = DemandStatus.New);
public record DemandDto(Guid Id, Guid ProjectId, string Title, string Description, DemandStatus Status, DateTime CreatedAt);

public record WorkItemInput(Guid DemandId, string Title, string Description = "", string AcceptanceCriteria = "",
    int Estimate = 0, WorkItemState State = WorkItemState.Backlog, List<Guid>? DependsOnIds = null);
public record WorkItemDto(Guid Id, Guid DemandId, string Title, string Description, string AcceptanceCriteria,
    int Estimate, WorkItemState State, int ReworkCount, string? BranchName, string? BlockedReason,
    List<Guid> DependsOnIds, DateTime CreatedAt, string? WaitingReason = null, string? PullRequestUrl = null, int? PullRequestNumber = null);

public record AgentRunDto(Guid Id, Guid? DemandId, Guid? WorkItemId, AgentRole Role, string Prompt, string Output,
    decimal CostUsd, long DurationMs, AgentRunStatus Status, DateTime StartedAt, DateTime? FinishedAt);

/// <summary>Versão leve para listas e timelines: sem prompt e sem saída (GET por id traz tudo).</summary>
public record AgentRunSummaryDto(Guid Id, Guid? DemandId, Guid? WorkItemId, AgentRole Role, decimal CostUsd,
    long DurationMs, AgentRunStatus Status, DateTime StartedAt, DateTime? FinishedAt);

public record ReviewFindingInput(Guid WorkItemId, AgentRole Role, FindingSeverity Severity, string Message,
    string? File = null, int? Line = null, Guid? AgentRunId = null, bool Resolved = false);
public record ReviewFindingDto(Guid Id, Guid WorkItemId, Guid? AgentRunId, AgentRole Role, FindingSeverity Severity,
    string? File, int? Line, string Message, bool Resolved);

public record RoleConfigInput(AgentRole Role, string AdapterName, string SystemPrompt, string AllowedTools,
    string Model, int MaxTurns);
public record RoleConfigDto(Guid Id, AgentRole Role, string AdapterName, string SystemPrompt, string AllowedTools,
    string Model, int MaxTurns);
