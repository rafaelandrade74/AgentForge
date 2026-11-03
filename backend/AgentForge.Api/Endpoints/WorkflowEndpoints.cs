using AgentForge.Orchestrator.Workflow;

namespace AgentForge.Api.Endpoints;

public record RejectRequest(string? Comment);

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        var demands = api.MapGroup("/demands").WithTags("Workflow");
        demands.MapPost("/{id:guid}/start", async (Guid id, WorkflowActions a) => Reply(await a.StartDemandAsync(id)))
            .WithSummary("Inicia o refinamento da demanda pelo Scrum Master.");
        demands.MapPost("/{id:guid}/approve-plan", async (Guid id, WorkflowActions a) => Reply(await a.ApprovePlanAsync(id)))
            .WithSummary("Aprovação humana do plano: libera as tarefas em Backlog.");

        var items = api.MapGroup("/work-items").WithTags("Workflow");
        items.MapPost("/{id:guid}/approve", async (Guid id, WorkflowActions a) => Reply(await a.ApproveAsync(id)))
            .WithSummary("Aprova a tarefa (Backlog → A fazer, ou Aguardando aprovação → Done).");
        items.MapPost("/{id:guid}/reject", async (Guid id, RejectRequest? body, WorkflowActions a) => Reply(await a.RejectAsync(id, body?.Comment)))
            .WithSummary("Rejeita a entrega e devolve ao Dev, com comentário.");
        items.MapPost("/{id:guid}/retry", async (Guid id, WorkflowActions a) => Reply(await a.RetryAsync(id)))
            .WithSummary("Tira a tarefa de Blocked e devolve ao Dev.");
        items.MapPost("/{id:guid}/cancel", async (Guid id, WorkflowActions a) => Reply(await a.CancelAsync(id)))
            .WithSummary("Cancela a tarefa (mata o agente em execução e remove o worktree e o branch).");
    }

    private static IResult Reply(ActionOutcome outcome) => outcome switch
    {
        ActionOutcome.Ok => Results.Accepted(),
        ActionOutcome.NotFound => Results.NotFound(),
        _ => Results.Conflict(new { error = "A ação não é válida para o estado atual." }),
    };
}
