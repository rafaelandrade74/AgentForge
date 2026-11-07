using System.Collections.Concurrent;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Orchestrator.Environments;
using Microsoft.Extensions.Options;

namespace AgentForge.Orchestrator.Workspaces;

public class WorkspaceOptions
{
    public const string Section = "Workspaces";

    /// <summary>
    /// Onde ficam os clones: {Root}/{projectId}/repo e {Root}/{projectId}/worktrees/{workItemId}. É um caminho do lado
    /// em que o git e os agentes rodam (no modo "host", um caminho do computador, não do container). Projetos com um
    /// ambiente próprio usam a pasta de clones desse ambiente.
    /// </summary>
    public string Root { get; set; } = "/data/repos";
}

/// <param name="BaseCommit">Commit de onde o trabalho da tarefa começa (o base, mais o trabalho das dependências já combinado).</param>
public record Workspace(string Path, string Branch, string BaseCommit);

/// <summary>Não foi possível montar o workspace (ex.: conflito ao combinar o trabalho das dependências).</summary>
public class WorkspaceException(string message) : Exception(message);

public interface IWorkspaceManager
{
    /// <summary>Clona o repositório do projeto na primeira vez; nas seguintes, só atualiza (fetch).</summary>
    Task<string> EnsureRepositoryAsync(Project project, CancellationToken ct);

    /// <summary>
    /// Cria (ou reaproveita, em retrabalho) o worktree da tarefa no branch agentforge/task-&lt;id&gt;. Uma tarefa nova nasce
    /// do branch base e **combina o trabalho das dependências** (os branches delas, já concluídas), para enxergar o código
    /// que depende; conflito aborta a criação (nada fica para trás) com <see cref="WorkspaceException"/>.
    /// </summary>
    Task<Workspace> CreateWorktreeAsync(Project project, Guid workItemId, CancellationToken ct,
        IReadOnlyList<Guid>? dependsOn = null, string? baseCommit = null);

    /// <summary>Remove o worktree. O branch é mantido (guarda o trabalho para o merge), salvo se deleteBranch.</summary>
    Task RemoveWorktreeAsync(Project project, Guid workItemId, bool deleteBranch, CancellationToken ct);

    /// <summary>
    /// Diff do que a tarefa mudou: do <paramref name="baseCommit"/> (se informado) ou do branch base até o branch da tarefa.
    /// Vazio se o branch ainda não existe.
    /// </summary>
    Task<string> GetDiffAsync(Project project, Guid workItemId, CancellationToken ct, string? baseCommit = null);

    /// <summary>
    /// Publica o branch agentforge/task-&lt;id&gt; da tarefa em <paramref name="pushUrl"/> (https, com a credencial embutida; some
    /// de qualquer mensagem de erro). O refspec é fixo — branch da tarefa para o mesmo nome — então nunca atualiza a base.
    /// </summary>
    Task PushTaskBranchAsync(Project project, Guid workItemId, string pushUrl, CancellationToken ct);
}

/// <summary>
/// Um clone por projeto e um git worktree por tarefa, cada um no seu branch. O clone recebe um pushurl inválido,
/// então nenhum agente consegue enviar nada ao remoto (nem para o branch base). Tudo roda no ambiente do projeto
/// (a máquina e a pasta de clones dele).
/// </summary>
public class WorkspaceManager(IOptions<WorkspaceOptions> options, IEnvironmentLaunchers environments) : IWorkspaceManager
{
    public const string NoPushUrl = "disabled://agentforge-never-push";

    /// <summary>Um ambiente só, com o launcher dado (testes e instalações sem ambientes cadastrados).</summary>
    public WorkspaceManager(IOptions<WorkspaceOptions> options, IProcessLauncher launcher)
        : this(options, new SingleEnvironment(launcher)) { }

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public static string BranchName(Guid workItemId) => $"agentforge/task-{workItemId}";

    // barras normais funcionam também no Windows, e deixam o caminho igual quando a API (Linux) fala com um host Windows
    private string Root(EnvironmentTarget t) =>
        (string.IsNullOrWhiteSpace(t.WorkspaceRoot) ? options.Value.Root : t.WorkspaceRoot).Replace('\\', '/').TrimEnd('/');

    private string RepoPath(EnvironmentTarget t, Project p) => $"{Root(t)}/{p.Id}/repo";
    private string WorktreePath(EnvironmentTarget t, Project p, Guid workItemId) => $"{Root(t)}/{p.Id}/worktrees/{workItemId}";
    private static string Parent(string path) => path[..path.LastIndexOf('/')];

    private static Task<string> Git(EnvironmentTarget t, string cwd, IEnumerable<string> args, CancellationToken ct) =>
        GitCli.RunAsync(t.Launcher, cwd, args, ct);

    private static Task<bool> HasRepoAsync(EnvironmentTarget t, string repo, CancellationToken ct) =>
        t.Launcher.DirectoryExistsAsync($"{repo}/.git", ct);

    // Operações git que mexem no mesmo repositório (clone, fetch, worktree add/remove) rodam uma por vez por projeto.
    private async Task<T> LockedAsync<T>(Project project, CancellationToken ct, Func<EnvironmentTarget, Task<T>> action)
    {
        var gate = _locks.GetOrAdd(project.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await action(await environments.ForProjectAsync(project, ct)); }
        finally { gate.Release(); }
    }

    public Task<string> EnsureRepositoryAsync(Project project, CancellationToken ct) =>
        LockedAsync(project, ct, t => EnsureCoreAsync(t, project, ct));

    private async Task<string> EnsureCoreAsync(EnvironmentTarget t, Project project, CancellationToken ct)
    {
        var repo = RepoPath(t, project);
        if (await HasRepoAsync(t, repo, ct))
        {
            await Git(t, repo, ["fetch", "--prune", "origin"], ct);
            return repo;
        }

        var url = project.RepositoryUrl.Trim();
        if (url.Length == 0 || url.StartsWith('-'))
            throw new GitException("URL de repositório inválida.", -1);

        await t.Launcher.CreateDirectoryAsync(Parent(repo), ct);
        try
        {
            await Git(t, Parent(repo), ["clone", "--", url, repo], ct);
            await Git(t, repo, ["config", "remote.origin.pushurl", NoPushUrl], ct);
            await Git(t, repo, ["config", "user.name", "AgentForge"], ct);
            await Git(t, repo, ["config", "user.email", "agentforge@localhost"], ct);
        }
        catch
        {
            // não deixa um clone pela metade (sem .git completo) travando as próximas tentativas
            await t.Launcher.DeleteDirectoryAsync(repo, CancellationToken.None);
            throw;
        }
        return repo;
    }

    public Task<Workspace> CreateWorktreeAsync(Project project, Guid workItemId, CancellationToken ct,
        IReadOnlyList<Guid>? dependsOn = null, string? baseCommit = null) =>
        LockedAsync(project, ct, async t =>
        {
            var repo = await EnsureCoreAsync(t, project, ct);
            var branch = BranchName(workItemId);
            var dir = WorktreePath(t, project, workItemId);

            // retrabalho / retomada: o worktree (ou o branch) já existe com o trabalho feito
            if (await t.Launcher.DirectoryExistsAsync(dir, ct))
                return new Workspace(dir, branch, baseCommit ?? await MergeBaseAsync(t, repo, project, branch, ct));

            await t.Launcher.CreateDirectoryAsync(Parent(dir), ct);
            await Git(t, repo, ["worktree", "prune"], ct);
            if (await BranchExistsAsync(t, repo, branch, ct))
            {
                await Git(t, repo, ["worktree", "add", dir, branch], ct);
                return new Workspace(dir, branch, baseCommit ?? await MergeBaseAsync(t, repo, project, branch, ct));
            }

            await Git(t, repo, ["worktree", "add", "-b", branch, dir, $"origin/{project.BaseBranch}"], ct);
            try
            {
                foreach (var dependency in dependsOn ?? [])
                    await MergeDependencyAsync(t, repo, dir, dependency, ct);
            }
            catch (WorkspaceException)
            {
                // não deixa worktree nem branch pela metade: a próxima tentativa começa limpa
                await Git(t, repo, ["worktree", "remove", "--force", dir], CancellationToken.None);
                await Git(t, repo, ["branch", "-D", branch], CancellationToken.None);
                throw;
            }

            return new Workspace(dir, branch, (await Git(t, dir, ["rev-parse", "HEAD"], ct)).Trim());
        });

    private async Task MergeDependencyAsync(EnvironmentTarget t, string repo, string dir, Guid dependencyId, CancellationToken ct)
    {
        var dependencyBranch = BranchName(dependencyId);
        if (!await BranchExistsAsync(t, repo, dependencyBranch, ct))
            throw new WorkspaceException($"O branch da dependência ({dependencyBranch}) não existe mais; não dá para partir do trabalho dela.");
        try
        {
            await Git(t, dir, ["merge", "--no-edit", dependencyBranch], ct);
        }
        catch (GitException)
        {
            try { await Git(t, dir, ["merge", "--abort"], CancellationToken.None); } catch (GitException) { }
            throw new WorkspaceException(
                $"Conflito ao combinar o trabalho da dependência ({dependencyBranch}) com o branch base: resolva manualmente ou ajuste o plano.");
        }
    }

    /// <summary>Ponto de partida de um branch criado antes do BaseCommit existir: onde ele se separa do branch base.</summary>
    private static async Task<string> MergeBaseAsync(EnvironmentTarget t, string repo, Project project, string branch, CancellationToken ct) =>
        (await Git(t, repo, ["merge-base", $"origin/{project.BaseBranch}", branch], ct)).Trim();

    public Task RemoveWorktreeAsync(Project project, Guid workItemId, bool deleteBranch, CancellationToken ct) =>
        LockedAsync(project, ct, async t =>
        {
            var repo = RepoPath(t, project);
            if (!await HasRepoAsync(t, repo, ct)) return true;

            var dir = WorktreePath(t, project, workItemId);
            if (await t.Launcher.DirectoryExistsAsync(dir, ct))
                await Git(t, repo, ["worktree", "remove", "--force", dir], ct);
            await Git(t, repo, ["worktree", "prune"], ct);

            var branch = BranchName(workItemId);
            if (deleteBranch && await BranchExistsAsync(t, repo, branch, ct))
                await Git(t, repo, ["branch", "-D", branch], ct);
            return true;
        });

    public Task PushTaskBranchAsync(Project project, Guid workItemId, string pushUrl, CancellationToken ct)
    {
        if (!(pushUrl.StartsWith("https://", StringComparison.Ordinal) || pushUrl.StartsWith("file://", StringComparison.Ordinal))
            || pushUrl.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new WorkspaceException("URL de push inválida (só https, sem espaços).");
        return LockedAsync(project, ct, async t =>
        {
            var branch = BranchName(workItemId);
            // o repositório pode ter sido clonado por SSH (deploy key somente leitura): empurra para a URL explícita, e só este branch
            await Git(t, RepoPath(t, project), ["push", pushUrl, $"refs/heads/{branch}:refs/heads/{branch}"], ct);
            return 0;
        });
    }

    public const int MaxDiffChars = 500_000;

    public async Task<string> GetDiffAsync(Project project, Guid workItemId, CancellationToken ct, string? baseCommit = null)
    {
        var t = await environments.ForProjectAsync(project, ct);
        var repo = RepoPath(t, project);
        if (!await HasRepoAsync(t, repo, ct)) return "";
        var branch = BranchName(workItemId);
        if (!await BranchExistsAsync(t, repo, branch, ct)) return "";
        try
        {
            var diff = await Git(t, repo, baseCommit is null
                ? ["diff", $"origin/{project.BaseBranch}...{branch}"]
                : ["diff", baseCommit, branch], ct);
            return diff.Length <= MaxDiffChars ? diff : diff[..MaxDiffChars] + "\n… (diff truncado)";
        }
        catch (GitException ex)
        {
            return $"(não foi possível gerar o diff: {ex.Message})";
        }
    }

    private static async Task<bool> BranchExistsAsync(EnvironmentTarget t, string repo, string branch, CancellationToken ct)
    {
        try
        {
            await Git(t, repo, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], ct);
            return true;
        }
        catch (GitException ex) when (ex.ExitCode == 1)
        {
            return false;
        }
    }
}
