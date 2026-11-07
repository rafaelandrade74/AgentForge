using AgentForge.Orchestrator.Agents;
namespace AgentForge.Orchestrator.Workflow;

public class OrchestratorOptions
{
    public const string Section = "Orchestrator";

    /// <summary>Quantas tarefas (e demandas em refinamento) andam ao mesmo tempo. Com mais de 1, Tech Lead e Security revisam em paralelo.</summary>
    public int MaxConcurrency { get; set; } = 1;

    /// <summary>Máquina fora do ar: espera antes da 1ª nova tentativa (dobra a cada falha até <see cref="EnvironmentRetryMaxSeconds"/>).</summary>
    public double EnvironmentRetryInitialSeconds { get; set; } = 15;

    public double EnvironmentRetryMaxSeconds { get; set; } = 300;
}

/// <summary>Uma tarefa/demanda que espera a máquina do ambiente voltar (não está Blocked).</summary>
/// <param name="UsageLimit">Preenchido quando a espera é por limite de uso do agente (a tela mostra o horário do reset).</param>
public record WaitingEntry(bool IsDemand, Guid Id, string Reason, DateTime RetryAt, int Attempts, UsageLimit? UsageLimit = null);

/// <summary>Carga do orquestrador por ambiente (chave = id do ambiente ou "default"): para a tela Ambientes e o MCP.</summary>
public interface IEnvironmentLoad
{
    IReadOnlyList<EnvironmentSlotInfo> Slots();
    IReadOnlyList<WaitingEntry> Waiting();
}

public enum StepResult { Continue, Stop }

public enum ActionOutcome { Ok, NotFound, InvalidState }

/// <summary>Fila do orquestrador: quem muda o estado de algo (API, processadores) avisa por aqui.</summary>
public interface IWorkQueue
{
    void EnqueueWorkItem(Guid workItemId);
    void EnqueueDemand(Guid demandId);

    /// <summary>Cancela a execução em andamento da tarefa (se houver) e espera ela terminar.</summary>
    Task CancelRunningAsync(Guid workItemId);
}
