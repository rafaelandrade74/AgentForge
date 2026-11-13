using AgentForge.Api.GitHub;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Workflow;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Api.Endpoints;

public record RejectRequest(string? Comment);
public record PullRequestRequest(string? BaseBranch);

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
        demands.MapPost("/{id:guid}/push", async (Guid id, PullRequestService prs, CancellationToken ct) =>
            {
                try
                {
                    var demand = await prs.PushDemandAsync(id, ct);
                    return Results.Ok(new { demand.BranchName, demand.PushedAt });
                }
                catch (PullRequestException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
                }
            })
            .WithSummary("Faz o git push do branch da demanda concluída para o GitHub.");
        demands.MapPost("/{id:guid}/pull-request", async (Guid id, PullRequestRequest? body, PullRequestService prs, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await prs.OpenDemandPrAsync(id, body?.BaseBranch, ct));
                }
                catch (PullRequestException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
                }
            })
            .WithSummary("Publica o branch da demanda concluída e abre o pull request no GitHub.");

        var items = api.MapGroup("/work-items").WithTags("Workflow");
        items.MapGet("/{id:guid}/diff", async (Guid id, AppDbContext db, IWorkspaceManager workspaces, CancellationToken ct) =>
            {
                var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
                if (item is null) return Results.NotFound();
                var project = await db.Projects.AsNoTracking()
                    .FirstAsync(p => p.Id == db.Demands.First(d => d.Id == item.DemandId).ProjectId, ct);
                return Results.Text(await workspaces.GetDiffAsync(project, id, ct, item.BaseCommit), "text/plain; charset=utf-8");
            })
            .WithSummary("Diff do branch da tarefa contra o branch base (texto).");
        items.MapPost("/{id:guid}/approve", async (Guid id, WorkflowActions a) => Reply(await a.ApproveAsync(id)))
            .WithSummary("Aprova a tarefa (Backlog → A fazer, ou Aguardando aprovação → Done).");
        items.MapPost("/{id:guid}/reject", async (Guid id, RejectRequest? body, WorkflowActions a) => Reply(await a.RejectAsync(id, body?.Comment)))
            .WithSummary("Rejeita a entrega e devolve ao Dev, com comentário.");
        items.MapPost("/{id:guid}/push", async (Guid id, PullRequestService prs, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await prs.PushAsync(id, ct));
                }
                catch (PullRequestException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
                }
            })
            .WithSummary("Faz o git push do branch da tarefa concluída (Done) para o GitHub, com o token do projeto. Só o branch da tarefa; não abre PR.");
        items.MapPost("/{id:guid}/pull-request", async (Guid id, PullRequestRequest? body, PullRequestService prs, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await prs.OpenAsync(id, body?.BaseBranch, ct));
                }
                catch (PullRequestException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
                }
            })
            .WithSummary("Publica o branch da tarefa concluída e abre o pull request no GitHub (token do projeto; destino: o informado, o do projeto ou a base).");
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
