using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Workflow;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AgentForge.Api.Realtime;

/// <summary>
/// Hub de tempo real em /hubs/runs. O cliente entra no grupo de cada projeto que está vendo (JoinProject) e recebe:
/// "runEvent" (AgentEvent: log do agente e run_started/run_finished), "workItemChanged" e "demandChanged".
/// </summary>
[Authorize]
public class RunsHub : Hub
{
    public static string Group(Guid projectId) => $"project:{projectId}";
    public static string ChatGroup(Guid sessionId) => $"chat:{sessionId}";
    /// <summary>Quem está na página "Agentes ao vivo" recebe os eventos de execução de TODOS os projetos.</summary>
    public const string LiveGroup = "live";

    public Task JoinLive() => Groups.AddToGroupAsync(Context.ConnectionId, LiveGroup);

    public Task LeaveLive() => Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveGroup);

    public Task JoinChat(Guid sessionId) => Groups.AddToGroupAsync(Context.ConnectionId, ChatGroup(sessionId));

    public Task LeaveChat(Guid sessionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatGroup(sessionId));

    public Task JoinProject(Guid projectId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(projectId));

    public Task LeaveProject(Guid projectId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(projectId));
}

public class SignalRAgentEventPublisher(IHubContext<RunsHub> hub, ILogger<SignalRAgentEventPublisher> logger) : IAgentEventPublisher
{
    public async Task PublishAsync(AgentEvent evt, CancellationToken ct = default)
    {
        try
        {
            if (evt.ProjectId is { } projectId) await hub.Clients.Group(RunsHub.Group(projectId)).SendAsync("runEvent", evt, ct);
            await hub.Clients.Group(RunsHub.LiveGroup).SendAsync("liveRunEvent", evt, ct);
        }
        catch (Exception ex)
        {
            // tempo real é só observação: nunca derruba a execução do agente
            logger.LogWarning(ex, "Falha ao transmitir evento da execução {RunId}", evt.RunId);
        }
    }
}

public class SignalRWorkflowEventPublisher(IHubContext<RunsHub> hub, ILogger<SignalRWorkflowEventPublisher> logger) : IWorkflowEventPublisher
{
    public Task WorkItemChangedAsync(WorkItemChanged evt) => SendAsync(evt.ProjectId, "workItemChanged", evt);
    public Task DemandChangedAsync(DemandChanged evt) => SendAsync(evt.ProjectId, "demandChanged", evt);

    private async Task SendAsync(Guid projectId, string method, object payload)
    {
        try
        {
            await hub.Clients.Group(RunsHub.Group(projectId)).SendAsync(method, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao transmitir {Method}", method);
        }
    }
}
