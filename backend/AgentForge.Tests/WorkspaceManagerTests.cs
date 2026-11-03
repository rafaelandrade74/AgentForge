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

        _manager = new WorkspaceManager(Options.Create(new WorkspaceOptions { Root = Path.Combine(_temp, "repos") }));
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

    [Fact]
    public async Task Two_tasks_do_not_see_each_others_changes()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var wsA = await _manager.CreateWorktreeAsync(_project, a, default);
        var wsB = await _manager.CreateWorktreeAsync(_project, b, default);

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
        var before = await OriginHead();
        var ws = await _manager.CreateWorktreeAsync(_project, Guid.NewGuid(), default);
        await CommitAsync(ws, "x.txt", "x");

        await Assert.ThrowsAsync<GitException>(() => Git(ws.Path, "push", "origin", "HEAD:main"));
        await Assert.ThrowsAsync<GitException>(() => Git(ws.Path, "push", "origin", ws.Branch));

        Assert.Equal(before, await OriginHead());
        await Assert.ThrowsAsync<GitException>(() => Git(_origin, "rev-parse", "--verify", "--quiet", ws.Branch));
    }

    [Fact]
    public async Task Existing_worktree_is_reused_keeping_commits()
    {
        var id = Guid.NewGuid();
        var first = await _manager.CreateWorktreeAsync(_project, id, default);
        await CommitAsync(first, "a.txt", "a");

        var again = await _manager.CreateWorktreeAsync(_project, id, default);

        Assert.Equal(first, again);
        Assert.True(File.Exists(Path.Combine(again.Path, "a.txt")));
    }

    [Fact]
    public async Task Removing_keeps_the_branch_unless_asked_and_allows_recreating()
    {
        var id = Guid.NewGuid();
        var ws = await _manager.CreateWorktreeAsync(_project, id, default);
        await CommitAsync(ws, "a.txt", "a");
        var repo = await _manager.EnsureRepositoryAsync(_project, default);

        await _manager.RemoveWorktreeAsync(_project, id, deleteBranch: false, default);

        Assert.False(Directory.Exists(ws.Path));
        Assert.DoesNotContain(ws.Path.Replace('\\', '/'), (await Git(repo, "worktree", "list")).Replace('\\', '/'));
        Assert.Contains(ws.Branch, await Git(repo, "branch", "--list", ws.Branch));

        var recreated = await _manager.CreateWorktreeAsync(_project, id, default);
        Assert.True(File.Exists(Path.Combine(recreated.Path, "a.txt")), "o branch preservou o trabalho");

        await _manager.RemoveWorktreeAsync(_project, id, deleteBranch: true, default);
        Assert.Equal("", (await Git(repo, "branch", "--list", ws.Branch)).Trim());
    }

    [Fact]
    public async Task Diff_shows_the_task_branch_against_base_even_after_the_worktree_is_removed()
    {
        var id = Guid.NewGuid();
        Assert.Equal("", await _manager.GetDiffAsync(_project, id, default));   // sem clone ainda

        var ws = await _manager.CreateWorktreeAsync(_project, id, default);
        Assert.Equal("", await _manager.GetDiffAsync(_project, id, default));   // branch sem mudanças
        await CommitAsync(ws, "novo.txt", "conteudo novo\n");

        var diff = await _manager.GetDiffAsync(_project, id, default);
        Assert.Contains("+++ b/novo.txt", diff);
        Assert.Contains("+conteudo novo", diff);

        await _manager.RemoveWorktreeAsync(_project, id, deleteBranch: false, default);
        Assert.Contains("+++ b/novo.txt", await _manager.GetDiffAsync(_project, id, default));
        Assert.Equal("", await _manager.GetDiffAsync(_project, Guid.NewGuid(), default));
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
        var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();

        var results = await Task.WhenAll(ids.Select(id => _manager.CreateWorktreeAsync(_project, id, default)));

        Assert.Equal(6, results.Select(r => r.Path).Distinct().Count());
        Assert.All(results, r => Assert.True(File.Exists(Path.Combine(r.Path, "README.md"))));
    }

    [Fact]
    public async Task Unknown_base_branch_fails_with_clear_error()
    {
        var project = new Project { Name = "P", RepositoryUrl = _origin, BaseBranch = "nao-existe" };
        var ex = await Assert.ThrowsAsync<GitException>(() => _manager.CreateWorktreeAsync(project, Guid.NewGuid(), default));
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
