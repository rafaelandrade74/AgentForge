using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Workflow;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Api.GitHub;

/// <summary>
/// Publica sozinho o branch da tarefa assim que ela chega a Done (aprovada por uma pessoa, ou direto no modo automático). Só empurra
/// o branch da tarefa (nunca a base), pelo origin do clone (a mesma chave SSH do pull) ou, em projetos HTTPS, com o token do GitHub; com erro, registra o motivo na tarefa
/// (<c>PushError</c>) e o botão "Abrir PR" continua podendo tentar de novo. Não abre o PR: isso segue sendo uma ação humana.
/// </summary>
public sealed class AutoBranchPublisher(AppDbContext db, PullRequestService pullRequests, ILogger<AutoBranchPublisher> logger) : IBranchPublisher
{
    public async Task PublishAsync(Guid workItemId, CancellationToken ct)
    {
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (item is null || item.State != WorkItemState.Done || item.PushedAt is not null) return;

        var project = await db.Projects.FirstAsync(p => p.Id == db.Demands.First(d => d.Id == item.DemandId).ProjectId, ct);
        if (!PushUrlProvider.UsesSsh(project) && project.ProtectedGitHubToken.Length == 0)
        {
            item.PushError = "Branch não publicado: o projeto usa HTTPS; cadastre o token do GitHub no projeto (ou use a URL SSH com a chave da tela Git).";
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        try
        {
            await pullRequests.PushBranchAsync(item, project, ct);
        }
        catch (PullRequestException ex)
        {
            logger.LogWarning("Publicação automática do branch da tarefa {Task} falhou", workItemId);
            item.PushError = ex.Message.Length > 480 ? ex.Message[..480] : ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
