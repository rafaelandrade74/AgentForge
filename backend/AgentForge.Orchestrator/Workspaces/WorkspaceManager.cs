using System.Collections.Concurrent;
using AgentForge.Domain;
using AgentForge.Execution;
using Microsoft.Extensions.Options;

namespace AgentForge.Orchestrator.Workspaces;

public class WorkspaceOptions
{
    public const string Section = "Workspaces";

    /// <summary>
    /// Onde ficam os clones: {Root}/{projectId}/repo e {Root}/{projectId}/worktrees/{workItemId}. É um caminho do lado
    /// em que o git e os agentes rodam (no modo "host", um caminho do computador, não do container).
    /// </summary>
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
public class WorkspaceManager(IOptions<WorkspaceOptions> options, IProcessLauncher launcher) : IWorkspaceManager
{
    public const string NoPushUrl = "disabled://agentforge-never-push";

    // barras normais funcionam também no Windows, e deixam o caminho igual quando a API (Linux) fala com um host Windows
    private readonly string _root = options.Value.Root.Replace('\\', '/').TrimEnd('/');
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public static string BranchName(Guid workItemId) => $"agentforge/task-{workItemId}";

    private string RepoPath(Project p) => $"{_root}/{p.Id}/repo";
    private string WorktreePath(Project p, Guid workItemId) => $"{_root}/{p.Id}/worktrees/{workItemId}";
    private static string Parent(string path) => path[..path.LastIndexOf('/')];

    private Task<string> Git(string cwd, IEnumerable<string> args, CancellationToken ct) => GitCli.RunAsync(launcher, cwd, args, ct);
    private Task<bool> HasRepoAsync(string repo, CancellationToken ct) => launcher.DirectoryExistsAsync($"{repo}/.git", ct);

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
        if (await HasRepoAsync(repo, ct))
        {
            await Git(repo, ["fetch", "--prune", "origin"], ct);
            return repo;
        }

        var url = project.RepositoryUrl.Trim();
        if (url.Length == 0 || url.StartsWith('-'))
            throw new GitException("URL de repositório inválida.", -1);

        await launcher.CreateDirectoryAsync(Parent(repo), ct);
        try
        {
            await Git(Parent(repo), ["clone", "--", url, repo], ct);
            await Git(repo, ["config", "remote.origin.pushurl", NoPushUrl], ct);
            await Git(repo, ["config", "user.name", "AgentForge"], ct);
            await Git(repo, ["config", "user.email", "agentforge@localhost"], ct);
        }
        catch
        {
            // não deixa um clone pela metade (sem .git completo) travando as próximas tentativas
            await launcher.DeleteDirectoryAsync(repo, CancellationToken.None);
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

            if (await launcher.DirectoryExistsAsync(dir, ct)) return new Workspace(dir, branch);

            await launcher.CreateDirectoryAsync(Parent(dir), ct);
            await Git(repo, ["worktree", "prune"], ct);
            var branchExists = await BranchExistsAsync(repo, branch, ct);
            string[] args = branchExists
                ? ["worktree", "add", dir, branch]
                : ["worktree", "add", "-b", branch, dir, $"origin/{project.BaseBranch}"];
            await Git(repo, args, ct);
            return new Workspace(dir, branch);
        });

    public Task RemoveWorktreeAsync(Project project, Guid workItemId, bool deleteBranch, CancellationToken ct) =>
        LockedAsync(project, ct, async () =>
        {
            var repo = RepoPath(project);
            if (!await HasRepoAsync(repo, ct)) return true;

            var dir = WorktreePath(project, workItemId);
            if (await launcher.DirectoryExistsAsync(dir, ct))
                await Git(repo, ["worktree", "remove", "--force", dir], ct);
            await Git(repo, ["worktree", "prune"], ct);

            var branch = BranchName(workItemId);
            if (deleteBranch && await BranchExistsAsync(repo, branch, ct))
                await Git(repo, ["branch", "-D", branch], ct);
            return true;
        });

    public const int MaxDiffChars = 500_000;

    public async Task<string> GetDiffAsync(Project project, Guid workItemId, CancellationToken ct)
    {
        var repo = RepoPath(project);
        if (!await HasRepoAsync(repo, ct)) return "";
        var branch = BranchName(workItemId);
        if (!await BranchExistsAsync(repo, branch, ct)) return "";
        try
        {
            var diff = await Git(repo, ["diff", $"origin/{project.BaseBranch}...{branch}"], ct);
            return diff.Length <= MaxDiffChars ? diff : diff[..MaxDiffChars] + "\n… (diff truncado)";
        }
        catch (GitException ex)
        {
            return $"(não foi possível gerar o diff: {ex.Message})";
        }
    }

    private async Task<bool> BranchExistsAsync(string repo, string branch, CancellationToken ct)
    {
        try
        {
            await Git(repo, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], ct);
            return true;
        }
        catch (GitException ex) when (ex.ExitCode == 1)
        {
            return false;
        }
    }
}
