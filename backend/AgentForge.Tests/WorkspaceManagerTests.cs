using AgentForge.Domain;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.Extensions.Options;

namespace AgentForge.Tests;

/// <summary>Usa o git de verdade contra um repositório "remoto" bare criado em disco temporário.</summary>
public sealed class WorkspaceManagerTests : IAsyncLifetime
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-ws-" + Guid.NewGuid().ToString("N"));
    private string _origin = "";
    private WorkspaceManager _manager = null!;
    private Project _project = null!;
    private readonly Guid _demandId = Guid.NewGuid();

    private static Task<string> Git(string dir, params string[] args) =>
        GitCli.RunAsync(dir, args, CancellationToken.None);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        var seed = Path.Combine(_temp, "seed");
        _origin = Path.Combine(_temp, "origin.git");
        Directory.CreateDirectory(seed);

        await Git(seed, "init", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(seed, "README.md"), "base\n");
        await Git(seed, "add", ".");
        await Git(seed, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "inicial");
        await Git(_temp, "clone", "--bare", seed, _origin);

        _manager = new WorkspaceManager(Options.Create(new WorkspaceOptions { Root = Path.Combine(_temp, "repos") }), new AgentForge.Execution.LocalProcessLauncher());
        _project = new Project { Name = "P", RepositoryUrl = _origin, BaseBranch = "main" };
    }

    public Task DisposeAsync()
    {
        // arquivos de objetos do git são somente leitura no Windows
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<string> OriginHead(string branch = "main") =>
        (await Git(_origin, "rev-parse", branch)).Trim();

    private async Task CommitAsync(Workspace ws, string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(ws.Path, file), content);
        await Git(ws.Path, "add", ".");
        await Git(ws.Path, "commit", "-m", $"add {file}");
    }

    private async Task<string> EnsureDemandBranch() =>
        await _manager.EnsureDemandBranchAsync(_project, _demandId, default);

    [Fact]
    public async Task Clones_once_and_only_fetches_afterwards()
    {
        var first = await _manager.EnsureRepositoryAsync(_project, default);
        await File.WriteAllTextAsync(Path.Combine(first, ".marcador"), "x");

        // novo commit no remoto
        var other = Path.Combine(_temp, "other");
        await Git(_temp, "clone", _origin, other);
        await File.WriteAllTextAsync(Path.Combine(other, "novo.txt"), "n");
        await Git(other, "add", ".");
        await Git(other, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "novo");
        await Git(other, "push", "origin", "main");

        var second = await _manager.EnsureRepositoryAsync(_project, default);

        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(second, ".marcador")), "não deve reclonar");
        Assert.Equal(await OriginHead(), (await Git(second, "rev-parse", "origin/main")).Trim());
    }

    private static async Task<(int Code, string Err)> TryGitAsync(string dir, params string[] args)
    {
        try { await Git(dir, args); return (0, ""); }
        catch (GitException ex) { return (ex.ExitCode, ex.Message); }
    }

    [Fact]
    public async Task Installs_executable_pre_push_hook_also_in_existing_clones()
    {
        if (OperatingSystem.IsWindows()) return;
        var repo = await _manager.EnsureRepositoryAsync(_project, default);
        var hook = Path.Combine(repo, ".git", "hooks", "pre-push");
        Assert.True(File.Exists(hook));
        Assert.True(File.GetUnixFileMode(hook).HasFlag(UnixFileMode.UserExecute));

        // clone antigo: hook ausente/adulterado volta a ser instalado
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nexit 0\n");
        await _manager.EnsureRepositoryAsync(_project, default);
        Assert.Equal(WorkspaceManager.PrePushHook, await File.ReadAllTextAsync(hook));
    }

    [Fact]
    public async Task Pre_push_hook_allows_task_and_demand_branches()
    {
        if (OperatingSystem.IsWindows()) return;
        await EnsureDemandBranch();
        var ws = await _manager.CreateWorktreeAsync(_project, Guid.NewGuid(), _demandId, default);
        await CommitAsync(ws, "a.txt", "a");
        await Git(ws.Path, "tag", "v1");

        // task branch allowed
        Assert.Equal(0, (await TryGitAsync(ws.Path, "push", _origin, $"{ws.Branch}:refs/heads/{ws.Branch}")).Code);

        // demand branch allowed
        var demandBranch = WorkspaceManager.DemandBranchName(_demandId);
        Assert.Equal(0, (await TryGitAsync(ws.Path, "push", _origin, $"HEAD:refs/heads/{demandBranch}")).Code);

        // protected branches rejected
        foreach (var target in new[] { "refs/heads/main", "refs/heads/develop", "refs/heads/outro", "refs/tags/v1" })
        {
            var r = await TryGitAsync(ws.Path, "push", _origin, $"HEAD:{target}");
            Assert.NotEqual(0, r.Code);
        }
        var del = await TryGitAsync(ws.Path, "push", _origin, $":refs/heads/{ws.Branch}");
        Assert.NotEqual(0, del.Code);
        Assert.Contains("deleção", del.Err);
    }

    [Fact]
    public async Task Two_tasks_do_not_see_each_others_changes()
    {
        await EnsureDemandBranch();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var wsA = await _manager.CreateWorktreeAsync(_project, a, _demandId, default);
        var wsB = await _manager.CreateWorktreeAsync(_project, b, _demandId, default);

        await CommitAsync(wsA, "so-da-a.txt", "A");
        await File.WriteAllTextAsync(Path.Combine(wsA.Path, "sujo-da-a.txt"), "não commitado");
        await CommitAsync(wsB, "so-da-b.txt", "B");

        Assert.NotEqual(wsA.Path, wsB.Path);
        Assert.Equal($"agentforge/task-{a}", wsA.Branch);
        Assert.Equal($"agentforge/task-{b}", wsB.Branch);
        Assert.False(File.Exists(Path.Combine(wsB.Path, "so-da-a.txt")));
        Assert.False(File.Exists(Path.Combine(wsB.Path, "sujo-da-a.txt")));
        Assert.False(File.Exists(Path.Combine(wsA.Path, "so-da-b.txt")));
        Assert.False(File.Exists(Path.Combine(await _manager.EnsureRepositoryAsync(_project, default), "so-da-a.txt")));
        Assert.Equal(wsA.Branch, (await Git(wsA.Path, "branch", "--show-current")).Trim());
        Assert.Equal(wsB.Branch, (await Git(wsB.Path, "branch", "--show-current")).Trim());
    }

    [Fact]
    public async Task Nothing_reaches_the_base_branch_and_push_is_blocked()
    {
        await EnsureDemandBranch();
        var before = await OriginHead();
        var ws = await _manager.CreateWorktreeAsync(_project, Guid.NewGuid(), _demandId, default);
        await CommitAsync(ws, "x.txt", "x");

        await Assert.ThrowsAsync<GitException>(() => Git(ws.Path, "push", "origin", "HEAD:main"));
        await Assert.ThrowsAsync<GitException>(() => Git(ws.Path, "push", "origin", ws.Branch));

        Assert.Equal(before, await OriginHead());
        await Assert.ThrowsAsync<GitException>(() => Git(_origin, "rev-parse", "--verify", "--quiet", ws.Branch));
    }

    [Fact]
    public async Task Existing_worktree_is_reused_keeping_commits()
    {
        await EnsureDemandBranch();
        var id = Guid.NewGuid();
        var first = await _manager.CreateWorktreeAsync(_project, id, _demandId, default);
        await CommitAsync(first, "a.txt", "a");

        var again = await _manager.CreateWorktreeAsync(_project, id, _demandId, default);

        Assert.Equal(first, again);
        Assert.True(File.Exists(Path.Combine(again.Path, "a.txt")));
    }

    [Fact]
    public async Task Removing_keeps_the_branch_unless_asked_and_allows_recreating()
    {
        await EnsureDemandBranch();
        var id = Guid.NewGuid();
        var ws = await _manager.CreateWorktreeAsync(_project, id, _demandId, default);
        await CommitAsync(ws, "a.txt", "a");
        var repo = await _manager.EnsureRepositoryAsync(_project, default);

        await _manager.RemoveWorktreeAsync(_project, id, deleteBranch: false, default);

        Assert.False(Directory.Exists(ws.Path));
        Assert.DoesNotContain(ws.Path.Replace('\\', '/'), (await Git(repo, "worktree", "list")).Replace('\\', '/'));
        Assert.Contains(ws.Branch, await Git(repo, "branch", "--list", ws.Branch));

        var recreated = await _manager.CreateWorktreeAsync(_project, id, _demandId, default);
        Assert.True(File.Exists(Path.Combine(recreated.Path, "a.txt")), "o branch preservou o trabalho");

        await _manager.RemoveWorktreeAsync(_project, id, deleteBranch: true, default);
        Assert.Equal("", (await Git(repo, "branch", "--list", ws.Branch)).Trim());
    }

    [Fact]
    public async Task Diff_shows_the_task_branch_against_base_even_after_the_worktree_is_removed()
    {
        await EnsureDemandBranch();
        var id = Guid.NewGuid();
        Assert.Equal("", await _manager.GetDiffAsync(_project, id, default));   // sem clone ainda

        var ws = await _manager.CreateWorktreeAsync(_project, id, _demandId, default);
        Assert.Equal("", await _manager.GetDiffAsync(_project, id, default));   // branch sem mudanças
        await CommitAsync(ws, "novo.txt", "conteudo novo\n");

        var diff = await _manager.GetDiffAsync(_project, id, default);
        Assert.Contains("+++ b/novo.txt", diff);
        Assert.Contains("+conteudo novo", diff);

        await _manager.RemoveWorktreeAsync(_project, id, deleteBranch: false, default);
        Assert.Contains("+++ b/novo.txt", await _manager.GetDiffAsync(_project, id, default));
        Assert.Equal("", await _manager.GetDiffAsync(_project, Guid.NewGuid(), default));
    }

    // ------------------------------------------------------------------ demand branch tests

    [Fact]
    public async Task EnsureDemandBranch_creates_branch_from_base()
    {
        var branch = await _manager.EnsureDemandBranchAsync(_project, _demandId, default);
        Assert.Equal($"agentforge/demand-{_demandId}", branch);

        var repo = await _manager.EnsureRepositoryAsync(_project, default);
        var sha = (await Git(repo, "rev-parse", branch)).Trim();
        Assert.Equal(await OriginHead(), sha);
    }

    [Fact]
    public async Task EnsureDemandBranch_is_idempotent()
    {
        var first = await _manager.EnsureDemandBranchAsync(_project, _demandId, default);
        var second = await _manager.EnsureDemandBranchAsync(_project, _demandId, default);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task CreateWorktree_branches_from_demand_branch()
    {
        await EnsureDemandBranch();
        var ws = await _manager.CreateWorktreeAsync(_project, Guid.NewGuid(), _demandId, default);
        Assert.True(File.Exists(Path.Combine(ws.Path, "README.md")));
    }

    [Fact]
    public async Task MergeTaskIntoDemand_incorporates_task_work()
    {
        await EnsureDemandBranch();
        var taskId = Guid.NewGuid();
        var ws = await _manager.CreateWorktreeAsync(_project, taskId, _demandId, default);
        await CommitAsync(ws, "feature.txt", "nova feature\n");
        await _manager.RemoveWorktreeAsync(_project, taskId, deleteBranch: false, default);

        await _manager.MergeTaskIntoDemandAsync(_project, _demandId, taskId, default);

        // verify demand branch has the file
        var repo = await _manager.EnsureRepositoryAsync(_project, default);
        var demandBranch = WorkspaceManager.DemandBranchName(_demandId);
        var content = await Git(repo, "show", $"{demandBranch}:feature.txt");
        Assert.Equal("nova feature\n", content);
    }

    [Fact]
    public async Task Second_task_sees_first_tasks_work_via_demand_branch()
    {
        await EnsureDemandBranch();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        // task A: create file, finish, merge into demand
        var wsA = await _manager.CreateWorktreeAsync(_project, a, _demandId, default);
        await CommitAsync(wsA, "da-a.txt", "A\n");
        await _manager.RemoveWorktreeAsync(_project, a, deleteBranch: false, default);
        await _manager.MergeTaskIntoDemandAsync(_project, _demandId, a, default);

        // task B: branches from demand, already has A's work
        var wsB = await _manager.CreateWorktreeAsync(_project, b, _demandId, default);
        Assert.True(File.Exists(Path.Combine(wsB.Path, "da-a.txt")), "B deve enxergar o código de A via demand branch");

        await CommitAsync(wsB, "da-b.txt", "B\n");
        var own = await _manager.GetDiffAsync(_project, b, default, wsB.BaseCommit);
        Assert.Contains("da-b.txt", own);
        Assert.DoesNotContain("da-a.txt", own);  // só o trabalho próprio
    }

    [Fact]
    public async Task MergeTaskIntoDemand_conflict_throws_WorkspaceException()
    {
        await EnsureDemandBranch();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        // both tasks start from the SAME demand branch (before either is merged)
        var wsA = await _manager.CreateWorktreeAsync(_project, a, _demandId, default);
        var wsB = await _manager.CreateWorktreeAsync(_project, b, _demandId, default);

        await CommitAsync(wsA, "mesmo.txt", "versão da A\n");
        await CommitAsync(wsB, "mesmo.txt", "versão da B\n");

        await _manager.RemoveWorktreeAsync(_project, a, deleteBranch: false, default);
        await _manager.RemoveWorktreeAsync(_project, b, deleteBranch: false, default);

        // A merges fine
        await _manager.MergeTaskIntoDemandAsync(_project, _demandId, a, default);

        // B conflicts with A's version already in demand
        var ex = await Assert.ThrowsAsync<WorkspaceException>(
            () => _manager.MergeTaskIntoDemandAsync(_project, _demandId, b, default));
        Assert.Contains("Conflito", ex.Message);
    }

    [Fact]
    public async Task PushDemandBranch_pushes_to_remote()
    {
        await EnsureDemandBranch();
        var taskId = Guid.NewGuid();
        var ws = await _manager.CreateWorktreeAsync(_project, taskId, _demandId, default);
        await CommitAsync(ws, "f.txt", "f\n");
        await _manager.RemoveWorktreeAsync(_project, taskId, deleteBranch: false, default);
        await _manager.MergeTaskIntoDemandAsync(_project, _demandId, taskId, default);

        await _manager.PushDemandBranchAsync(_project, _demandId, "file://" + _origin, default);

        var demandBranch = WorkspaceManager.DemandBranchName(_demandId);
        var remote = (await Git(_origin, "rev-parse", demandBranch)).Trim();
        var repo = await _manager.EnsureRepositoryAsync(_project, default);
        var local = (await Git(repo, "rev-parse", demandBranch)).Trim();
        Assert.Equal(local, remote);
    }

    [Fact]
    public async Task Existing_worktree_keeps_the_recorded_base_commit_and_legacy_ones_fall_back_to_the_merge_base()
    {
        await EnsureDemandBranch();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await CommitAsync(await _manager.CreateWorktreeAsync(_project, a, _demandId, default), "a.txt", "a\n");
        await _manager.RemoveWorktreeAsync(_project, a, deleteBranch: false, default);
        await _manager.MergeTaskIntoDemandAsync(_project, _demandId, a, default);
        var wsB = await _manager.CreateWorktreeAsync(_project, b, _demandId, default);

        var again = await _manager.CreateWorktreeAsync(_project, b, _demandId, default, wsB.BaseCommit);
        var legacy = await _manager.CreateWorktreeAsync(_project, b, _demandId, default);   // sem BaseCommit conhecido

        Assert.Equal(wsB.BaseCommit, again.BaseCommit);
        Assert.Equal(await OriginHead(), legacy.BaseCommit);                      // volta ao comportamento antigo
    }

    [Fact]
    public async Task Removing_something_that_does_not_exist_is_a_noop()
    {
        await _manager.RemoveWorktreeAsync(_project, Guid.NewGuid(), true, default);
        await _manager.EnsureRepositoryAsync(_project, default);
        await _manager.RemoveWorktreeAsync(_project, Guid.NewGuid(), true, default);
    }

    [Fact]
    public async Task Parallel_creation_works()
    {
        await EnsureDemandBranch();
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();

        var results = await Task.WhenAll(ids.Select(id => _manager.CreateWorktreeAsync(_project, id, _demandId, default)));

        Assert.Equal(6, results.Select(r => r.Path).Distinct().Count());
        Assert.All(results, r => Assert.True(File.Exists(Path.Combine(r.Path, "README.md"))));
    }

    [Fact]
    public async Task Unknown_base_branch_fails_with_clear_error()
    {
        var project = new Project { Name = "P", RepositoryUrl = _origin, BaseBranch = "nao-existe" };
        var ex = await Assert.ThrowsAsync<GitException>(() => _manager.EnsureDemandBranchAsync(project, Guid.NewGuid(), default));
        Assert.Contains("origin/nao-existe", ex.Message);
    }

    [Theory]
    [InlineData("--upload-pack=touch /tmp/pwned")]
    [InlineData("")]
    public async Task Rejects_repository_urls_that_look_like_options(string url)
    {
        var project = new Project { Name = "P", RepositoryUrl = url };
        await Assert.ThrowsAsync<GitException>(() => _manager.EnsureRepositoryAsync(project, default));
    }

    [Fact]
    public async Task Failed_clone_leaves_nothing_behind_so_a_retry_works()
    {
        var project = new Project { Name = "P", RepositoryUrl = Path.Combine(_temp, "nao-existe.git"), BaseBranch = "main" };
        await Assert.ThrowsAsync<GitException>(() => _manager.EnsureRepositoryAsync(project, default));

        project.RepositoryUrl = _origin;
        var repo = await _manager.EnsureRepositoryAsync(project, default);

        Assert.True(Directory.Exists(Path.Combine(repo, ".git")));
    }
}
