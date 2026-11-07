using AgentForge.Domain;

namespace AgentForge.Orchestrator.Agents;

/// <summary>Tudo que o runner precisa para executar um agente em um diretório de trabalho.</summary>
public record AgentRequest(
    AgentRole Role,
    string Prompt,
    string WorkingDirectory,
    string AdapterName = "claude-code",
    string SystemPrompt = "",
    string AllowedTools = "",
    string Model = "",
    int MaxTurns = 20,
    Guid? DemandId = null,
    Guid? WorkItemId = null,
    TimeSpan? Timeout = null,
    Guid? ProjectId = null,
    /// <summary>Execução avulsa (ex.: teste da conexão): não grava AgentRun nem publica eventos.</summary>
    bool Ephemeral = false,
    /// <summary>Ambiente onde rodar quando não há projeto (ex.: teste de conexão de uma máquina). Com projeto, vale o do projeto.</summary>
    Guid? EnvironmentId = null,
    /// <summary>
    /// Rodar em <see cref="EnvironmentId"/> (null = padrão do servidor) MESMO havendo projeto: papel movido para outro ambiente
    /// (Tech Lead/Security numa máquina leve). Sem o worktree do projeto: o diretório de trabalho é o temporário da máquina e
    /// não há container de perfil.
    /// </summary>
    bool EnvironmentOverride = false)
{
    public static AgentRequest FromConfig(AgentRoleConfig config, string prompt, string workingDirectory,
        Guid? demandId = null, Guid? workItemId = null, TimeSpan? timeout = null, Guid? projectId = null,
        Guid? environmentId = null, bool environmentOverride = false) =>
        new(config.Role, prompt, workingDirectory, config.AdapterName, config.SystemPrompt, config.AllowedTools,
            config.Model, config.MaxTurns, demandId, workItemId, timeout, projectId, EnvironmentId: environmentId,
            EnvironmentOverride: environmentOverride);
}

/// <summary>Evento interpretado de uma linha de saída do CLI (ex.: "assistant", "tool", "result").</summary>
public record AgentEventData(string Kind, string Text);

/// <summary>
/// Evento publicado durante a execução, já ligado ao AgentRun. Além dos eventos do CLI (assistant, tool, ...),
/// o runner emite "run_started" e "run_finished" (Text = status final) para o front acompanhar o ciclo.
/// </summary>
public record AgentEvent(Guid RunId, Guid? ProjectId, Guid? DemandId, Guid? WorkItemId, AgentRole Role, string Kind, string Text, DateTime At);

/// <summary>Resultado final lido da saída do CLI.</summary>
/// <param name="Limit">Preenchido quando o erro é limite de uso da conta (não falha do trabalho): vale esperar, não falhar.</param>
public record AgentResult(string Text, string? Json, decimal CostUsd, bool IsError, UsageLimit? Limit = null);

public record AgentRunResult(
    Guid RunId,
    AgentRunStatus Status,
    string Output,
    string? Json,
    decimal CostUsd,
    TimeSpan Duration,
    int? ExitCode,
    /// <summary>Falhou porque a máquina do ambiente não respondeu (vale tentar de novo quando voltar), não por erro do agente.</summary>
    bool EnvironmentDown = false,
    /// <summary>Falhou porque a conta do agente atingiu o limite de uso: espera até liberar em vez de contar como falha.</summary>
    UsageLimit? UsageLimit = null);

public interface IAgentRunner
{
    Task<AgentRunResult> RunAsync(AgentRequest request, CancellationToken ct);
}

/// <summary>
/// O que muda de um CLI de agente para outro. Todo o resto (processo, streaming, timeout,
/// persistência) fica no CliAgentRunner. O prompt é enviado pela entrada padrão.
/// </summary>
public interface ICliAgentAdapter
{
    string Name { get; }
    string Executable { get; }
    IReadOnlyList<string> BuildArguments(AgentRequest request);
    /// <summary>Interpreta uma linha de stdout; null para linhas sem interesse.</summary>
    AgentEventData? ParseEvent(string line);
    /// <summary>Monta o resultado final a partir de todas as linhas de stdout e do código de saída.</summary>
    AgentResult ParseResult(IReadOnlyList<string> stdoutLines, int exitCode);
}

/// <summary>Recebe os eventos de execução; a Fase 6 troca a implementação padrão por SignalR.</summary>
public interface IAgentEventPublisher
{
    Task PublishAsync(AgentEvent evt, CancellationToken ct = default);
}

public class NullAgentEventPublisher : IAgentEventPublisher
{
    public Task PublishAsync(AgentEvent evt, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Adapters registrados no DI, escolhidos pelo nome configurado em AgentRoleConfig.</summary>
public class AgentAdapterRegistry(IEnumerable<ICliAgentAdapter> adapters)
{
    private readonly Dictionary<string, ICliAgentAdapter> _byName =
        adapters.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<ICliAgentAdapter> All => _byName.Values;

    public ICliAgentAdapter? TryGet(string name) => _byName.GetValueOrDefault(name);

    public ICliAgentAdapter Get(string name) =>
        _byName.TryGetValue(name, out var adapter)
            ? adapter
            : throw new InvalidOperationException($"Adapter de CLI '{name}' não está registrado.");
}
