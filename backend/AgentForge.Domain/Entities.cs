namespace AgentForge.Domain;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string BaseBranch { get; set; } = "main";
    public string BuildCommand { get; set; } = "";
    public string TestCommand { get; set; } = "";
    /// <summary>Branch de destino dos pull requests das tarefas concluídas (ex.: develop). Vazio = <see cref="BaseBranch"/>.</summary>
    public string PullRequestBaseBranch { get; set; } = "";
    /// <summary>
    /// Token do GitHub (escrita em Contents e Pull requests só neste repositório) usado para fazer push do branch da tarefa e abrir o
    /// PR. Guardado protegido (Data Protection), nunca devolvido nem logado. Vazio = sem PR pelo AgentForge.
    /// </summary>
    public string ProtectedGitHubToken { get; set; } = "";
    /// <summary>Quando true, desliga as aprovações humanas do fluxo (modo automático).</summary>
    public bool AutoMode { get; set; }
    /// <summary>Onde os agentes e o git deste projeto rodam. Null = o ambiente padrão do servidor (Agents:Launcher).</summary>
    public Guid? EnvironmentId { get; set; }
    /// <summary>Se definido, os agentes deste projeto rodam num container deste perfil (só em ambiente com host agent com Docker).</summary>
    public Guid? StackProfileId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Uma máquina onde os CLIs (agentes e git) rodam, por um host agent. O ambiente padrão (o container da API ou o
/// que Agents:Launcher define) não é gravado: é o que vale quando o projeto não escolhe nenhum.
/// </summary>
public class ExecutionEnvironment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public EnvironmentKind Kind { get; set; } = EnvironmentKind.HostAgent;
    /// <summary>Endereço do host agent visto pela API (ex.: http://192.168.0.20:8787).</summary>
    public string Url { get; set; } = "";
    /// <summary>Token do host agent, protegido (ASP.NET Data Protection). Nunca sai da API nem entra em log.</summary>
    public string ProtectedToken { get; set; } = "";
    /// <summary>Pasta, NESSA máquina, onde ficam clones e worktrees: {raiz}/{projeto}/repo e .../worktrees/{tarefa}.</summary>
    public string WorkspaceRoot { get; set; } = "";
    /// <summary>Rótulos livres da stack disponível (ex.: "node20, dotnet8, docker").</summary>
    public string Tags { get; set; } = "";
    /// <summary>Quantas tarefas (de projetos deste ambiente) rodam ao mesmo tempo nesta máquina; as demais esperam na fila do ambiente.</summary>
    public int MaxConcurrency { get; set; } = 1;
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
    /// <summary>Commit de onde o trabalho da tarefa começa: o branch base mais o trabalho das dependências já combinado.</summary>
    public string? BaseCommit { get; set; }
    /// <summary>Por que a tarefa está em Blocked (falha do agente, limite de retrabalho...).</summary>
    public string? BlockedReason { get; set; }
    /// <summary>A máquina do ambiente está fora do ar: a tarefa continua no estado atual e será retomada quando ela voltar (não é Blocked).</summary>
    public string? WaitingReason { get; set; }
    /// <summary>Pull request aberto no GitHub com o branch desta tarefa (depois de concluída e aprovada).</summary>
    public string? PullRequestUrl { get; set; }
    public int? PullRequestNumber { get; set; }
    /// <summary>Quando o branch da tarefa foi publicado no GitHub (automático ao chegar em Done, ou ao abrir o PR). Nulo = ainda não.</summary>
    public DateTime? PushedAt { get; set; }
    /// <summary>Por que a publicação automática não aconteceu (ex.: projeto sem token do GitHub). Nunca contém o token.</summary>
    public string? PushError { get; set; }
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
    /// <summary>Ambiente onde a execução rodou (null = padrão do servidor). Sem chave estrangeira: o histórico sobrevive ao ambiente.</summary>
    public Guid? EnvironmentId { get; set; }
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

/// <summary>Token de acesso de longa duração (ex.: para um cliente MCP). Guardamos só o hash; vale apenas em /mcp.</summary>
public class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    /// <summary>Primeiros caracteres do token, só para a pessoa reconhecê-lo na lista.</summary>
    public string Prefix { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TokenHash { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// Perfil de stack: a imagem Docker (ou o Dockerfile) em que os agentes de um projeto rodam, com limites. O container sobe
/// por tarefa no host agent, com só a pasta do projeto montada; sem --privileged e sem o socket do Docker.
/// </summary>
public class StackProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Imagem pronta (ex.: minha/imagem:1). Vazia quando há Dockerfile.</summary>
    public string Image { get; set; } = "";
    /// <summary>Conteúdo do Dockerfile, construído na máquina do ambiente (sem contexto de arquivos). Vazio quando há Image.</summary>
    public string Dockerfile { get; set; } = "";
    /// <summary>"bridge" (com rede) ou "none" (sem rede).</summary>
    public string Network { get; set; } = "bridge";
    /// <summary>Limite de CPUs do container; 0 = sem limite.</summary>
    public double CpuLimit { get; set; }
    /// <summary>Limite de memória em MB; 0 = sem limite.</summary>
    public int MemoryMb { get; set; }
    /// <summary>Perfis prontos (Node, .NET, Python) são recriados no start e não podem ser editados nem apagados.</summary>
    public bool BuiltIn { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Papel que roda em OUTRO ambiente que o do projeto (ex.: Tech Lead e Security numa máquina leve). Só papéis que trabalham sobre o
/// diff (Tech Lead e Security) podem ser movidos: Dev e QA precisam do worktree, que mora no ambiente do projeto.
/// </summary>
public class ProjectRoleEnvironment
{
    public Guid ProjectId { get; set; }
    public AgentRole Role { get; set; }
    public Guid EnvironmentId { get; set; }
}

public enum ChatRole { User, Assistant }

/// <summary>
/// Conversa com o agente de uma máquina (ambiente) sobre um projeto. Serve para tirar dúvidas e para o agente PROPOR demandas, que
/// só viram demandas quando a pessoa clica em criar. <see cref="ProviderSessionId"/> é a sessão do CLI (claude --resume).
/// </summary>
public class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public Guid ProjectId { get; set; }
    /// <summary>Máquina cujo agente conversa. Null = o ambiente padrão do servidor. Sem FK: o ambiente pode ser apagado e a conversa fica.</summary>
    public Guid? EnvironmentId { get; set; }
    public string AdapterName { get; set; } = "claude-code";
    public string? ProviderSessionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChatSessionId { get; set; }
    public ChatRole Role { get; set; }
    public string Text { get; set; } = "";
    /// <summary>A resposta falhou (agente sem login, limite de uso, erro): o texto explica; a pessoa pode reenviar.</summary>
    public bool Failed { get; set; }
    /// <summary>Demandas que o agente propôs nesta resposta (JSON: [{title, description, demandId?}]). demandId preenchido = já criada.</summary>
    public string? Proposals { get; set; }
    public decimal CostUsd { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
