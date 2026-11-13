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

    /// <summary>Cria (ou garante a existência do) branch agentforge/demand-&lt;id&gt; a partir de origin/{baseBranch}. Idempotente.</summary>
    Task<string> EnsureDemandBranchAsync(Project project, Guid demandId, CancellationToken ct);

    /// <summary>
    /// Cria (ou reaproveita, em retrabalho) o worktree da tarefa no branch agentforge/task-&lt;id&gt;, partindo do branch
    /// da demanda (que acumula o trabalho das tarefas anteriores).
    /// </summary>
    Task<Workspace> CreateWorktreeAsync(Project project, Guid workItemId, Guid demandId, CancellationToken ct,
        string? baseCommit = null);

    /// <summary>Merge o branch da tarefa concluída no branch da demanda. Retorna o novo HEAD da demanda.</summary>
    Task<string> MergeTaskIntoDemandAsync(Project project, Guid demandId, Guid workItemId, CancellationToken ct);

    /// <summary>Remove o worktree. O branch é mantido (guarda o trabalho para o merge), salvo se deleteBranch.</summary>
    Task RemoveWorktreeAsync(Project project, Guid workItemId, bool deleteBranch, CancellationToken ct);

    /// <summary>
    /// Diff do que a tarefa mudou: do <paramref name="baseCommit"/> (se informado) ou do branch base até o branch da tarefa.
    /// Vazio se o branch ainda não existe.
    /// </summary>
    Task<string> GetDiffAsync(Project project, Guid workItemId, CancellationToken ct, string? baseCommit = null);

    /// <summary>
    /// Publica o branch agentforge/task-&lt;id&gt; da tarefa em <paramref name="pushUrl"/>: https com a credencial embutida (some de
    /// qualquer mensagem de erro) ou a URL SSH do projeto (git@host:dono/repo.git), que usa a MESMA chave do clone/pull. O refspec é
    /// fixo — branch da tarefa para o mesmo nome — então nunca atualiza a base. O `origin` do clone continua sem push (NoPushUrl).
    /// </summary>
    Task PushTaskBranchAsync(Project project, Guid workItemId, string pushUrl, CancellationToken ct);

    /// <summary>Publica o branch agentforge/demand-&lt;id&gt; no remoto.</summary>
    Task PushDemandBranchAsync(Project project, Guid demandId, string pushUrl, CancellationToken ct);

    /// <summary>
    /// true se o remoto (<paramref name="pushUrl"/>) já tem o branch da tarefa exatamente no commit local (o agente já publicou).
    /// Qualquer falha na checagem (remoto fora, sem acesso, URL inválida) devolve false — o chamador segue para o push normal.
    /// Nunca lança por falha de git, então não expõe URL/token.
    /// </summary>
    Task<bool> IsBranchPublishedAsync(Project project, Guid workItemId, string pushUrl, CancellationToken ct);

    /// <summary>true se o remoto já tem o branch da demanda no mesmo commit local.</summary>
    Task<bool> IsDemandBranchPublishedAsync(Project project, Guid demandId, string pushUrl, CancellationToken ct);
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
    public static string DemandBranchName(Guid demandId) => $"agentforge/demand-{demandId}";

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
            await InstallPrePushHookAsync(t, repo, ct);
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
            await InstallPrePushHookAsync(t, repo, ct);
        }
        catch
        {
            // não deixa um clone pela metade (sem .git completo) travando as próximas tentativas
            await t.Launcher.DeleteDirectoryAsync(repo, CancellationToken.None);
            throw;
        }
        return repo;
    }

    /// <summary>
    /// Script do hook pre-push: só deixa passar refs/heads/agentforge/task-*. Rejeita main/develop, outros branches,
    /// tags e deleção de ref. Fica em &lt;repo&gt;/.git/hooks (o .git comum), então vale em todos os worktrees.
    /// </summary>
    public const string PrePushHook = """
        #!/bin/sh
        # AgentForge: só é permitido publicar branches agentforge/task-* e agentforge/demand-*.
        zero=0000000000000000000000000000000000000000
        while read -r local_ref local_sha remote_ref remote_sha; do
            if [ "$local_sha" = "$zero" ]; then
                echo "AgentForge: push rejeitado - deleção de ref ($remote_ref) não é permitida." >&2
                exit 1
            fi
            case "$remote_ref" in
                refs/heads/agentforge/task-?*) ;;
                refs/heads/agentforge/demand-?*) ;;
                refs/heads/main|refs/heads/develop)
                    echo "AgentForge: push rejeitado - o branch $remote_ref é protegido; só refs/heads/agentforge/{task,demand}-* podem ser enviados." >&2
                    exit 1 ;;
                refs/tags/*)
                    echo "AgentForge: push rejeitado - tags ($remote_ref) não são permitidas; só refs/heads/agentforge/{task,demand}-* podem ser enviados." >&2
                    exit 1 ;;
                *)
                    echo "AgentForge: push rejeitado - $remote_ref não casa com refs/heads/agentforge/{task,demand}-*; só esses branches podem ser enviados." >&2
                    exit 1 ;;
            esac
        done
        exit 0

        """;

    // Grava (e dá chmod +x) pelo próprio launcher: via Launcher.WriteFileAsync (local ou host agent).
    // Idempotente: reescreve a cada chamada, valendo também para clones antigos.
    private static async Task InstallPrePushHookAsync(EnvironmentTarget t, string repo, CancellationToken ct)
    {
        try { await t.Launcher.WriteFileAsync($"{repo}/.git/hooks/pre-push", PrePushHook, executable: true, ct); }
        catch (HostUnreachableException) { throw; }
        catch (Exception ex) when (ex is ProcessStartException or IOException or UnauthorizedAccessException)
        {
            throw new GitException($"Não foi possível instalar o hook pre-push: {ex.Message}", -1);
        }
    }

    public Task<string> EnsureDemandBranchAsync(Project project, Guid demandId, CancellationToken ct) =>
        LockedAsync(project, ct, async t =>
        {
            var repo = await EnsureCoreAsync(t, project, ct);
            var branch = DemandBranchName(demandId);
            if (!await BranchExistsAsync(t, repo, branch, ct))
                await Git(t, repo, ["branch", branch, $"origin/{project.BaseBranch}"], ct);
            return branch;
        });

    public Task<Workspace> CreateWorktreeAsync(Project project, Guid workItemId, Guid demandId, CancellationToken ct,
        string? baseCommit = null) =>
        LockedAsync(project, ct, async t =>
        {
            var repo = await EnsureCoreAsync(t, project, ct);
            var branch = BranchName(workItemId);
            var demandBranch = DemandBranchName(demandId);
            var dir = WorktreePath(t, project, workItemId);

            if (await t.Launcher.DirectoryExistsAsync(dir, ct))
                return new Workspace(dir, branch, baseCommit ?? await MergeBaseAsync(t, repo, project, branch, ct));

            await t.Launcher.CreateDirectoryAsync(Parent(dir), ct);
            await Git(t, repo, ["worktree", "prune"], ct);
            if (await BranchExistsAsync(t, repo, branch, ct))
            {
                await Git(t, repo, ["worktree", "add", dir, branch], ct);
                return new Workspace(dir, branch, baseCommit ?? await MergeBaseAsync(t, repo, project, branch, ct));
            }

            if (!await BranchExistsAsync(t, repo, demandBranch, ct))
                await Git(t, repo, ["branch", demandBranch, $"origin/{project.BaseBranch}"], ct);
            await Git(t, repo, ["worktree", "add", "-b", branch, dir, demandBranch], ct);
            return new Workspace(dir, branch, (await Git(t, dir, ["rev-parse", "HEAD"], ct)).Trim());
        });

    public Task<string> MergeTaskIntoDemandAsync(Project project, Guid demandId, Guid workItemId, CancellationToken ct) =>
        LockedAsync(project, ct, async t =>
        {
            var repo = await EnsureCoreAsync(t, project, ct);
            var demandBranch = DemandBranchName(demandId);
            var taskBranch = BranchName(workItemId);
            var mergeDir = $"{Root(t)}/{project.Id}/worktrees/_demand-merge-{demandId}";

            await Git(t, repo, ["worktree", "prune"], ct);
            if (await t.Launcher.DirectoryExistsAsync(mergeDir, ct))
                await Git(t, repo, ["worktree", "remove", "--force", mergeDir], CancellationToken.None);

            await t.Launcher.CreateDirectoryAsync(Parent(mergeDir), ct);
            await Git(t, repo, ["worktree", "add", mergeDir, demandBranch], ct);
            try
            {
                await Git(t, mergeDir, ["merge", "--no-edit", taskBranch], ct);
                var head = (await Git(t, mergeDir, ["rev-parse", "HEAD"], ct)).Trim();
                return head;
            }
            catch (GitException)
            {
                try { await Git(t, mergeDir, ["merge", "--abort"], CancellationToken.None); } catch (GitException) { }
                throw new WorkspaceException(
                    $"Conflito ao fazer merge da tarefa ({taskBranch}) no branch da demanda ({demandBranch}): resolva manualmente.");
            }
            finally
            {
                try { await Git(t, repo, ["worktree", "remove", "--force", mergeDir], CancellationToken.None); } catch { }
            }
        });

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

    /// <summary>https/file, ssh://... ou o formato scp do git (usuario@host:dono/repo.git); começa por letra/dígito (nunca vira opção do git).</summary>
    private static bool IsSafePushUrl(string url) =>
        url.Length > 0 && char.IsAsciiLetterOrDigit(url[0]) && !url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        && (url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("file://", StringComparison.Ordinal)
            || url.StartsWith("ssh://", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(url, @"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[A-Za-z0-9._/~-]+$"));

    public Task PushTaskBranchAsync(Project project, Guid workItemId, string pushUrl, CancellationToken ct)
    {
        if (!IsSafePushUrl(pushUrl))
            throw new WorkspaceException("URL de push inválida (use https ou a URL SSH do repositório, sem espaços).");
        return LockedAsync(project, ct, async t =>
        {
            var branch = BranchName(workItemId);
            // o origin do clone não aceita push (NoPushUrl): empurra para a URL explícita, e só este branch. Em URL SSH, o git usa a
            // mesma chave do clone/pull (GIT_SSH_COMMAND do launcher; nos modos 1 e 3, as credenciais do git do computador).
            await Git(t, RepoPath(t, project), ["push", pushUrl, $"refs/heads/{branch}:refs/heads/{branch}"], ct);
            return 0;
        });
    }

    public Task PushDemandBranchAsync(Project project, Guid demandId, string pushUrl, CancellationToken ct)
    {
        if (!IsSafePushUrl(pushUrl))
            throw new WorkspaceException("URL de push inválida (use https ou a URL SSH do repositório, sem espaços).");
        return LockedAsync(project, ct, async t =>
        {
            var branch = DemandBranchName(demandId);
            await Git(t, RepoPath(t, project), ["push", pushUrl, $"refs/heads/{branch}:refs/heads/{branch}"], ct);
            return 0;
        });
    }

    public async Task<bool> IsDemandBranchPublishedAsync(Project project, Guid demandId, string pushUrl, CancellationToken ct)
    {
        if (!IsSafePushUrl(pushUrl)) return false;
        try
        {
            return await LockedAsync(project, ct, async t =>
            {
                var repo = RepoPath(t, project);
                var branch = $"refs/heads/{DemandBranchName(demandId)}";
                var local = (await Git(t, repo, ["rev-parse", "--verify", "--quiet", branch], ct)).Trim();
                if (local.Length == 0) return false;
                var remote = await Git(t, repo, ["ls-remote", pushUrl, branch], ct);
                var line = remote.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => l.EndsWith(branch, StringComparison.Ordinal));
                var sha = line?.Split('\t', ' ')[0];
                return sha is not null && string.Equals(sha, local, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex) when (ex is GitException or WorkspaceException or ProcessStartException or EnvironmentUnavailableException)
        {
            return false;
        }
    }

    public async Task<bool> IsBranchPublishedAsync(Project project, Guid workItemId, string pushUrl, CancellationToken ct)
    {
        if (!IsSafePushUrl(pushUrl)) return false;
        try
        {
            return await LockedAsync(project, ct, async t =>
            {
                var repo = RepoPath(t, project);
                var branch = $"refs/heads/{BranchName(workItemId)}";
                var local = (await Git(t, repo, ["rev-parse", "--verify", "--quiet", branch], ct)).Trim();
                if (local.Length == 0) return false;
                var remote = await Git(t, repo, ["ls-remote", pushUrl, branch], ct);
                var line = remote.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault(l => l.EndsWith(branch, StringComparison.Ordinal));
                var sha = line?.Split('\t', ' ')[0];
                return sha is not null && string.Equals(sha, local, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex) when (ex is GitException or WorkspaceException or ProcessStartException or EnvironmentUnavailableException)
        {
            return false;
        }
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
