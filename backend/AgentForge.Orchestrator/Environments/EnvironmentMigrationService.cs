using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Orchestrator.Environments;

public record MigrationResult(int BranchesPushed, int BranchesSkipped, List<string> Warnings);

public class MigrationException(string message) : Exception(message);

public class EnvironmentMigrationService(
    AppDbContext db, IWorkspaceManager workspaces, IPushUrlProvider pushUrls)
{
    private static readonly WorkItemState[] ActiveStates =
    [
        WorkItemState.InDevelopment, WorkItemState.TechLeadReview, WorkItemState.SecurityReview,
        WorkItemState.Qa, WorkItemState.AwaitingApproval, WorkItemState.Blocked,
    ];

    public async Task<MigrationResult> MigrateAsync(Guid projectId, Guid? targetEnvironmentId, CancellationToken ct)
    {
        var project = await db.Projects.FindAsync([projectId], ct)
            ?? throw new MigrationException("Projeto não encontrado.");

        if (project.EnvironmentId == targetEnvironmentId)
            throw new MigrationException("O ambiente de destino é o mesmo do projeto.");

        if (targetEnvironmentId is { } tid && !await db.ExecutionEnvironments.AnyAsync(e => e.Id == tid, ct))
            throw new MigrationException("Ambiente de destino não encontrado.");

        var projectDemandIds = await db.Demands.Where(d => d.ProjectId == projectId).Select(d => d.Id).ToListAsync(ct);

        var hasRunning = await db.AgentRuns.AnyAsync(r =>
            r.Status == AgentRunStatus.Running &&
            ((r.DemandId != null && projectDemandIds.Contains(r.DemandId.Value)) ||
             (r.WorkItemId != null && db.WorkItems.Any(w => w.Id == r.WorkItemId && projectDemandIds.Contains(w.DemandId)))),
            ct);
        if (hasRunning)
            throw new MigrationException("Há agentes rodando neste projeto. Aguarde o término antes de migrar.");

        var resolved = await pushUrls.ResolveAsync(project, ct);
        var pushUrl = resolved.Url ?? throw new MigrationException(
            $"Não há URL de push para o projeto. {resolved.Reason ?? "Configure o token do GitHub ou use uma URL SSH."}");

        // Coletar branches a transferir
        var demandBranches = await db.Demands
            .Where(d => d.ProjectId == projectId && d.BranchName != null &&
                (d.Status == DemandStatus.InProgress || (d.Status == DemandStatus.Done && d.PushedAt == null)))
            .Select(d => new { d.Id, d.BranchName, Kind = "demand" })
            .ToListAsync(ct);

        var taskBranches = await db.WorkItems
            .Where(w => projectDemandIds.Contains(w.DemandId) && w.BranchName != null &&
                (ActiveStates.Contains(w.State) || (w.State == WorkItemState.Done && w.PushedAt == null)))
            .Select(w => new { w.Id, w.BranchName, Kind = "task" })
            .ToListAsync(ct);

        var allBranches = demandBranches.Select(b => (b.Id, b.BranchName!, b.Kind))
            .Concat(taskBranches.Select(b => (b.Id, b.BranchName!, b.Kind)))
            .ToList();

        var warnings = new List<string>();
        int pushed = 0, skipped = 0;

        if (allBranches.Count > 0)
        {
            // Fetch no source antes de empurrar
            await workspaces.EnsureRepositoryAsync(project, ct);

            foreach (var (id, branch, kind) in allBranches)
            {
                if (!await workspaces.LocalBranchExistsAsync(project, branch, ct))
                {
                    skipped++;
                    continue;
                }
                try
                {
                    if (kind == "demand")
                        await workspaces.PushDemandBranchAsync(project, id, pushUrl, ct);
                    else
                        await workspaces.PushTaskBranchAsync(project, id, pushUrl, ct);
                    pushed++;
                }
                catch (Exception ex) when (ex is WorkspaceException or GitException)
                {
                    skipped++;
                    warnings.Add($"Não foi possível publicar {branch}: {ex.Message}");
                }
            }
        }

        // Atualizar ambiente do projeto
        var sourceEnvId = project.EnvironmentId;
        project.EnvironmentId = targetEnvironmentId;
        await db.SaveChangesAsync(ct);

        // Avisar sobre ProjectRoleEnvironment apontando para o ambiente antigo
        if (sourceEnvId is not null)
        {
            var roleCount = await db.ProjectRoleEnvironments.CountAsync(
                r => r.ProjectId == projectId && r.EnvironmentId == sourceEnvId, ct);
            if (roleCount > 0)
                warnings.Add($"Há {roleCount} papel(is) (Tech Lead/Security) ainda apontando para o ambiente anterior. Ajuste na tela do projeto se necessário.");
        }

        // Clone/fetch no target e criar branches locais
        try
        {
            await workspaces.EnsureRepositoryAsync(project, ct);
            var branchNames = allBranches.Select(b => b.Item2).ToList();
            if (branchNames.Count > 0)
                await workspaces.EnsureLocalBranchesFromRemoteAsync(project, branchNames, ct);
        }
        catch (Exception ex) when (ex is GitException or WorkspaceException or HostUnreachableException or EnvironmentUnavailableException)
        {
            warnings.Add($"Não foi possível preparar o repositório no ambiente de destino: {ex.Message}. Os branches já estão no origin e serão obtidos automaticamente na próxima execução.");
        }

        return new MigrationResult(pushed, skipped, warnings);
    }
}
