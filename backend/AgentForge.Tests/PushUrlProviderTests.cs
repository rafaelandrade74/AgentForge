using AgentForge.Api.GitHub;
using AgentForge.Domain;
using AgentForge.Orchestrator.Environments;

namespace AgentForge.Tests;

public sealed class PushUrlProviderTests
{
    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plain) => "enc:" + plain;
        public string Unprotect(string protectedText) => protectedText["enc:".Length..];
    }

    private static PushUrlProvider Provider() => new(new FakeProtector());

    [Fact]
    public async Task Ssh_project_returns_its_ssh_url()
    {
        var p = new Project { RepositoryUrl = " git@github.com:octo/repo.git " };
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
}
