using AgentForge.Api.GitHub;
using AgentForge.Domain;
using AgentForge.Orchestrator.Environments;

namespace AgentForge.Tests;

public sealed class PushUrlProviderTests
{
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plain) => "enc:" + plain;
        public string Unprotect(string protectedText) =>
            protectedText == "bad" ? throw new System.Security.Cryptography.CryptographicException() : protectedText["enc:".Length..];
    }

    private static PushUrlProvider Provider() => new(new FakeProtector());

    [Fact]
    public async Task Ssh_project_returns_its_ssh_url()
    {
        var p = new Project { RepositoryUrl = "git@github.com:octo/repo.git " };
        Assert.Equal("git@github.com:octo/repo.git", await Provider().ResolveAsync(p));
    }

    [Fact]
    public async Task Https_with_token_returns_url_with_access_token()
    {
        var p = new Project { RepositoryUrl = "https://github.com/octo/repo", ProtectedGitHubToken = "enc:tok123" };
        Assert.Equal("https://x-access-token:tok123@github.com/octo/repo.git", await Provider().ResolveAsync(p));
    }

    [Fact]
    public async Task Https_without_token_returns_null()
    {
        var p = new Project { RepositoryUrl = "https://github.com/octo/repo" };
        Assert.Null(await Provider().ResolveAsync(p));
    }

    [Fact]
    public async Task Undecryptable_token_becomes_PullRequestException()
    {
        var p = new Project { RepositoryUrl = "https://github.com/octo/repo", ProtectedGitHubToken = "bad" };
        await Assert.ThrowsAsync<PullRequestException>(() => Provider().ResolveAsync(p));
        Assert.Throws<PullRequestException>(() => Provider().ReadToken(p));
    }

    [Fact]
    public async Task Https_outside_github_returns_null()
    {
        var p = new Project { RepositoryUrl = "https://gitlab.com/octo/repo", ProtectedGitHubToken = "enc:tok" };
        Assert.Null(await Provider().ResolveAsync(p));
        Assert.Contains("github.com", PushUrlProvider.UnavailableReason(p));
    }

    [Fact]
    public void ReadToken_without_token_explains_how_to_register()
    {
        var ex = Assert.Throws<PullRequestException>(() => Provider().ReadToken(new Project { RepositoryUrl = "https://github.com/octo/repo" }));
        Assert.Contains("Cadastre o token", ex.Message);
    }
}
