using AgentForge.Api.GitHub;
using AgentForge.Domain;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.Tests;

public sealed class PushUrlProviderTests
{
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plain) => "enc:" + plain;
        public string Unprotect(string protectedText) =>
            protectedText == "bad" ? throw new System.Security.Cryptography.CryptographicException() : protectedText["enc:".Length..];
    }

    private sealed class FakeProvider(PushUrlResult result) : IPushUrlProvider
    {
        public Task<PushUrlResult> ResolveAsync(Project project, CancellationToken ct = default) => Task.FromResult(result);
    }

    private sealed class RecordingWorkspaces : IWorkspaceManager
    {
        public string? PushedUrl;
        public bool Published;
        public bool CheckThrows;
        public int Pushes;
        public Task<bool> IsBranchPublishedAsync(Project project, Guid workItemId, string pushUrl, CancellationToken ct) =>
            CheckThrows ? throw new InvalidOperationException("https://x:SEGREDO@h/r.git") : Task.FromResult(Published);
        public Task PushTaskBranchAsync(Project project, Guid workItemId, string pushUrl, CancellationToken ct)
        {
            PushedUrl = pushUrl;
            Pushes++;
            throw new WorkspaceException("parou aqui");   // evita tocar o banco: só queremos ver a URL usada
        }
        public Task<string> EnsureRepositoryAsync(Project project, CancellationToken ct) => throw new NotSupportedException();
        public Task<Workspace> CreateWorktreeAsync(Project project, Guid workItemId, CancellationToken ct, IReadOnlyList<Guid>? dependsOn = null, string? baseCommit = null) => throw new NotSupportedException();
        public Task RemoveWorktreeAsync(Project project, Guid workItemId, bool deleteBranch, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetDiffAsync(Project project, Guid workItemId, CancellationToken ct, string? baseCommit = null) => throw new NotSupportedException();
    }

    private static PushUrlProvider Provider() => new(new FakeProtector());

    private static PullRequestService Service(IPushUrlProvider provider, IWorkspaceManager workspaces) =>
        new(null!, workspaces, provider, new GitHubClient(new HttpClient()), NullLogger<PullRequestService>.Instance);

    [Fact]
    public async Task Ssh_project_returns_its_ssh_url()
    {
        var p = new Project { RepositoryUrl = "git@github.com:octo/repo.git " };
        var r = await Provider().ResolveAsync(p);
        Assert.Equal("git@github.com:octo/repo.git", r.Url);
        Assert.Null(r.Token);
    }

    [Fact]
    public async Task Https_with_token_returns_url_with_access_token()
    {
        var p = new Project { RepositoryUrl = "https://github.com/octo/repo", ProtectedGitHubToken = "enc:tok123" };
        var r = await Provider().ResolveAsync(p);
        Assert.Equal("https://x-access-token:tok123@github.com/octo/repo.git", r.Url);
        Assert.Equal("tok123", r.Token);
        Assert.Null(r.Reason);
    }

    [Fact]
    public async Task Https_without_token_returns_null_url_and_reason()
    {
        var r = await Provider().ResolveAsync(new Project { RepositoryUrl = "https://github.com/octo/repo" });
        Assert.Null(r.Url);
        Assert.Contains("Cadastre o token", r.Reason);
    }

    [Fact]
    public async Task Undecryptable_token_has_no_url_and_explains()
    {
        var r = await Provider().ResolveAsync(new Project { RepositoryUrl = "https://github.com/octo/repo", ProtectedGitHubToken = "bad" });
        Assert.Null(r.Url);
        Assert.Null(r.Token);
        Assert.Contains("chave de proteção", r.Reason);
    }

    [Fact]
    public async Task Https_outside_github_returns_null_url()
    {
        var r = await Provider().ResolveAsync(new Project { RepositoryUrl = "https://gitlab.com/octo/repo", ProtectedGitHubToken = "enc:tok" });
        Assert.Null(r.Url);
        Assert.Contains("github.com", r.Reason);
    }

    [Fact]
    public async Task PushBranch_without_url_throws_PullRequestException_with_the_reason()
    {
        var svc = Service(new FakeProvider(new PushUrlResult(null, null, "sem token")), new RecordingWorkspaces());
        var ex = await Assert.ThrowsAsync<PullRequestException>(() => svc.PushBranchAsync(new WorkItem(), new Project(), default));
        Assert.Equal("sem token", ex.Message);
    }

    [Fact]
    public async Task PushBranch_uses_the_url_from_the_provider()
    {
        var ws = new RecordingWorkspaces();
        var svc = Service(new FakeProvider(new PushUrlResult("git@github.com:octo/repo.git", null, null)), ws);
        await Assert.ThrowsAsync<PullRequestException>(() => svc.PushBranchAsync(new WorkItem(), new Project(), default));
        Assert.Equal("git@github.com:octo/repo.git", ws.PushedUrl);
    }

    [Fact]
    public async Task PushBranch_ja_publicado_nao_faz_push()
    {
        var ws = new RecordingWorkspaces { Published = true };
        var svc = Service(new FakeProvider(new PushUrlResult("git@github.com:octo/repo.git", null, null)), ws);
        // db nulo: o SaveChanges estoura depois de registrar; basta ver que o push não ocorreu
        await Assert.ThrowsAnyAsync<Exception>(() => svc.PushBranchAsync(new WorkItem(), new Project(), default));
        Assert.Equal(0, ws.Pushes);
    }

    [Fact]
    public async Task PushBranch_remoto_atras_ou_ausente_faz_push()
    {
        var ws = new RecordingWorkspaces { Published = false };
        var svc = Service(new FakeProvider(new PushUrlResult("git@github.com:octo/repo.git", null, null)), ws);
        await Assert.ThrowsAsync<PullRequestException>(() => svc.PushBranchAsync(new WorkItem(), new Project(), default));
        Assert.Equal(1, ws.Pushes);
    }

    [Fact]
    public async Task PushBranch_falha_na_checagem_cai_para_push_sem_vazar()
    {
        var ws = new RecordingWorkspaces { CheckThrows = true };
        var svc = Service(new FakeProvider(new PushUrlResult("git@github.com:octo/repo.git", null, null)), ws);
        var ex = await Assert.ThrowsAsync<PullRequestException>(() => svc.PushBranchAsync(new WorkItem(), new Project(), default));
        Assert.Equal(1, ws.Pushes);
        Assert.DoesNotContain("SEGREDO", ex.Message);
    }

    [Fact]
    public void Result_ToString_nao_vaza_segredo()
    {
        var text = new PushUrlResult("https://x-access-token:SEGREDO@github.com/o/r.git", "SEGREDO", null).ToString();
        Assert.DoesNotContain("SEGREDO", text);
    }
}
