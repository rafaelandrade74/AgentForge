namespace AgentForge.Orchestrator.Workflow;

public class OrchestratorOptions
{
    public const string Section = "Orchestrator";

    /// <summary>Quantas tarefas (e demandas em refinamento) andam ao mesmo tempo. Com mais de 1, Tech Lead e Security revisam em paralelo.</summary>
    public int MaxConcurrency { get; set; } = 1;
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
