using System.Collections.Concurrent;
using AgentForge.Domain;
using Microsoft.Extensions.Options;

namespace AgentForge.Orchestrator.Workspaces;

public class WorkspaceOptions
{
    public const string Section = "Workspaces";

    /// <summary>Volume onde ficam os clones: {Root}/{projectId}/repo e {Root}/{projectId}/worktrees/{workItemId}.</summary>
    public string Root { get; set; } = "/data/repos";
}

public record Workspace(string Path, string Branch);

public interface IWorkspaceManager
{
    /// <summary>Clona o repositório do projeto na primeira vez; nas seguintes, só atualiza (fetch).</summary>
    Task<string> EnsureRepositoryAsync(Project project, CancellationToken ct);

    /// <summary>Cria (ou reaproveita, em retrabalho) o worktree da tarefa no branch agentforge/task-&lt;id&gt;.</summary>
    Task<Workspace> CreateWorktreeAsync(Project project, Guid workItemId, CancellationToken ct);

    /// <summary>Remove o worktree. O branch é mantido (guarda o trabalho para o merge), salvo se deleteBranch.</summary>
    Task RemoveWorktreeAsync(Project project, Guid workItemId, bool deleteBranch, CancellationToken ct);

    /// <summary>Diff do branch da tarefa contra o branch base (vazio se o branch ainda não existe).</summary>
    Task<string> GetDiffAsync(Project project, Guid workItemId, CancellationToken ct);
}

/// <summary>
/// Um clone por projeto e um git worktree por tarefa, cada um no seu branch. O clone recebe um pushurl inválido,
/// então nenhum agente consegue enviar nada ao remoto (nem para o branch base).
/// </summary>
public class WorkspaceManager(IOptions<WorkspaceOptions> options) : IWorkspaceManager
{
    public const string NoPushUrl = "disabled://agentforge-never-push";

    private readonly string _root = options.Value.Root;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public static string BranchName(Guid workItemId) => $"agentforge/task-{workItemId}";

    private string RepoPath(Project p) => Path.Combine(_root, p.Id.ToString(), "repo");
    private string WorktreePath(Project p, Guid workItemId) => Path.Combine(_root, p.Id.ToString(), "worktrees", workItemId.ToString());

    // Operações git que mexem no mesmo repositório (clone, fetch, worktree add/remove) rodam uma por vez por projeto.
    private async Task<T> LockedAsync<T>(Project project, CancellationToken ct, Func<Task<T>> action)
    {
        var gate = _locks.GetOrAdd(project.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await action(); }
        finally { gate.Release(); }
    }

    public Task<string> EnsureRepositoryAsync(Project project, CancellationToken ct) =>
        LockedAsync(project, ct, () => EnsureCoreAsync(project, ct));

    private async Task<string> EnsureCoreAsync(Project project, CancellationToken ct)
    {
        var repo = RepoPath(project);
        if (Directory.Exists(Path.Combine(repo, ".git")))
        {
            await GitCli.RunAsync(repo, ["fetch", "--prune", "origin"], ct);
            return repo;
        }

        var url = project.RepositoryUrl.Trim();
        if (url.Length == 0 || url.StartsWith('-'))
            throw new GitException("URL de repositório inválida.", -1);

        Directory.CreateDirectory(Path.GetDirectoryName(repo)!);
        try
        {
            await GitCli.RunAsync(Path.GetDirectoryName(repo)!, ["clone", "--", url, repo], ct);
            await GitCli.RunAsync(repo, ["config", "remote.origin.pushurl", NoPushUrl], ct);
            await GitCli.RunAsync(repo, ["config", "user.name", "AgentForge"], ct);
            await GitCli.RunAsync(repo, ["config", "user.email", "agentforge@localhost"], ct);
        }
        catch
        {
            // não deixa um clone pela metade (sem .git completo) travando as próximas tentativas
            if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true);
            throw;
        }
        return repo;
    }

    public Task<Workspace> CreateWorktreeAsync(Project project, Guid workItemId, CancellationToken ct) =>
        LockedAsync(project, ct, async () =>
        {
            var repo = await EnsureCoreAsync(project, ct);
            var branch = BranchName(workItemId);
            var dir = WorktreePath(project, workItemId);

            if (Directory.Exists(dir)) return new Workspace(dir, branch);

            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            await GitCli.RunAsync(repo, ["worktree", "prune"], ct);
            var branchExists = await BranchExistsAsync(repo, branch, ct);
            string[] args = branchExists
                ? ["worktree", "add", dir, branch]
                : ["worktree", "add", "-b", branch, dir, $"origin/{project.BaseBranch}"];
            await GitCli.RunAsync(repo, args, ct);
            return new Workspace(dir, branch);
        });

    public Task RemoveWorktreeAsync(Project project, Guid workItemId, bool deleteBranch, CancellationToken ct) =>
        LockedAsync(project, ct, async () =>
        {
            var repo = RepoPath(project);
            if (!Directory.Exists(Path.Combine(repo, ".git"))) return true;

            var dir = WorktreePath(project, workItemId);
            if (Directory.Exists(dir))
                await GitCli.RunAsync(repo, ["worktree", "remove", "--force", dir], ct);
            await GitCli.RunAsync(repo, ["worktree", "prune"], ct);

            var branch = BranchName(workItemId);
            if (deleteBranch && await BranchExistsAsync(repo, branch, ct))
                await GitCli.RunAsync(repo, ["branch", "-D", branch], ct);
            return true;
        });

    public const int MaxDiffChars = 500_000;

    public async Task<string> GetDiffAsync(Project project, Guid workItemId, CancellationToken ct)
    {
        var repo = RepoPath(project);
        if (!Directory.Exists(Path.Combine(repo, ".git"))) return "";
        var branch = BranchName(workItemId);
        if (!await BranchExistsAsync(repo, branch, ct)) return "";
        try
        {
            var diff = await GitCli.RunAsync(repo, ["diff", $"origin/{project.BaseBranch}...{branch}"], ct);
            return diff.Length <= MaxDiffChars ? diff : diff[..MaxDiffChars] + "\n… (diff truncado)";
        }
        catch (GitException ex)
        {
            return $"(não foi possível gerar o diff: {ex.Message})";
        }
    }

    private static async Task<bool> BranchExistsAsync(string repo, string branch, CancellationToken ct)
    {
        try
        {
            await GitCli.RunAsync(repo, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], ct);
            return true;
        }
        catch (GitException ex) when (ex.ExitCode == 1)
        {
            return false;
        }
    }
}
