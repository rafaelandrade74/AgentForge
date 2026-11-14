using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AgentForge.Infrastructure.Persistence;

namespace AgentForge.Tests;

public sealed class PullRequestApiTests : IAsyncLifetime
{
    private TestApiFactory _factory = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _factory = new TestApiFactory();
        _http = await TestAuth.AuthenticatedClientAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<JsonElement> CreateProject(object? extra = null, string url = "https://github.com/octo/privado.git")
    {
        var body = new Dictionary<string, object?> { ["name"] = "P" + Guid.NewGuid().ToString("N")[..5], ["repositoryUrl"] = url, ["baseBranch"] = "main" };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var response = await _http.PostAsJsonAsync("/api/projects", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Guid Id(JsonElement project) => project.GetProperty("id").GetGuid();

    [Fact]
    public async Task The_token_is_stored_protected_never_returned_and_can_be_kept_replaced_or_cleared()
    {
        const string token = "ghp_token_de_teste_1234567890abcdef";
        var project = await CreateProject(new { githubToken = token, pullRequestBaseBranch = "develop" });
        var id = Id(project);
        Assert.True(project.GetProperty("hasGitHubToken").GetBoolean());
        Assert.Equal("develop", project.GetProperty("pullRequestBaseBranch").GetString());
        Assert.DoesNotContain(token, project.GetRawText());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == id);
            Assert.NotEmpty(stored.ProtectedGitHubToken);
            Assert.DoesNotContain(token, stored.ProtectedGitHubToken);
        }

        var edit = new Dictionary<string, object?> { ["name"] = "P", ["repositoryUrl"] = "https://github.com/octo/privado.git", ["baseBranch"] = "main" };
        var kept = await (await _http.PutAsJsonAsync($"/api/projects/{id}", edit)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(kept.GetProperty("hasGitHubToken").GetBoolean());
        Assert.Equal("develop", kept.GetProperty("pullRequestBaseBranch").GetString());

        edit["githubToken"] = "";
        var cleared = await (await _http.PutAsJsonAsync($"/api/projects/{id}", edit)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(cleared.GetProperty("hasGitHubToken").GetBoolean());

        edit["githubToken"] = token;
        edit["pullRequestBaseBranch"] = "";
        var replaced = await (await _http.PutAsJsonAsync($"/api/projects/{id}", edit)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(replaced.GetProperty("hasGitHubToken").GetBoolean());
        Assert.Equal("", replaced.GetProperty("pullRequestBaseBranch").GetString());
    }

    [Fact]
    public async Task Invalid_tokens_and_branch_names_are_rejected()
    {
        foreach (var bad in new object[]
        {
            new { githubToken = "curto" }, new { githubToken = "tem espaco no meio do token 123456789" }, new { githubToken = "tem@arroba/e-barra-123456789012345" },
            new { pullRequestBaseBranch = "--delete" }, new { pullRequestBaseBranch = "a..b" }, new { pullRequestBaseBranch = "com espaco" },
        })
        {
            var body = new Dictionary<string, object?> { ["name"] = "X", ["repositoryUrl"] = "https://github.com/octo/privado.git" };
            foreach (var p in bad.GetType().GetProperties()) body[p.Name] = p.GetValue(bad);
            Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/projects", body)).StatusCode);
        }
    }
}
