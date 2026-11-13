namespace AgentForge.Orchestrator.Workflow;

/// <summary>
/// Publica o branch de uma tarefa concluída no repositório remoto. Quem faz o push é o servidor (nunca o agente) e só do branch da
/// tarefa, nunca da base. A implementação real fica na API (usa o token do GitHub do projeto); por padrão não faz nada.
/// Não deve lançar: o resultado fica em <c>WorkItem.PushedAt</c> / <c>PushError</c>, e a tarefa continua Done de qualquer jeito.
/// </summary>
public interface IBranchPublisher
{
    Task PublishAsync(Guid workItemId, CancellationToken ct);
    Task PublishDemandAsync(Guid demandId, CancellationToken ct);
}

public sealed class NullBranchPublisher : IBranchPublisher
{
    public Task PublishAsync(Guid workItemId, CancellationToken ct) => Task.CompletedTask;
    public Task PublishDemandAsync(Guid demandId, CancellationToken ct) => Task.CompletedTask;
}
