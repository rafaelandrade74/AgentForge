using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentForge.Api.GitHub;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests;

public sealed class FakeGitHub(string cloneUrl = "https://github.com/octo/publico.git") : HttpMessageHandler
{
    public const string Token = "ghp_token_de_teste_1234567890abcdef";
    public record Seen(string Method, string Path, string? Authorization, string Body);
    public ConcurrentQueue<Seen> Requests { get; } = new();
    public bool DeployKeyAlreadyInUse { get; set; }
    public bool RepoMissing { get; set; }
    public bool PullAlreadyExists { get; set; }
    public bool PullRejected { get; set; }

    private object Repo(string full, bool isPrivate) => new
    {
        full_name = full, name = full.Split('/')[1], @private = isPrivate, default_branch = "main", description = "d",
        clone_url = cloneUrl, ssh_url = cloneUrl, updated_at = "2026-01-01T00:00:00Z", permissions = new { admin = true },
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.PathAndQuery.TrimStart('/');
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue(new Seen(request.Method.Method, path, request.Headers.Authorization?.ToString(), body));

        if (request.Headers.Authorization is { Parameter: { } p } && p != Token && path.StartsWith("user/repos"))
            return Json(HttpStatusCode.Unauthorized, new { message = "Bad credentials" });

        if (request.Method == HttpMethod.Post && path.EndsWith("/pulls"))
        {
            if (PullRejected) return Json(HttpStatusCode.UnprocessableEntity, new { message = "Validation Failed", errors = new[] { new { message = "base invalid" } } });
            if (PullAlreadyExists)
                return Json(HttpStatusCode.UnprocessableEntity, new { message = "Validation Failed", errors = new[] { new { message = "A pull request already exists for octo:agentforge/task." } } });
            return Json(HttpStatusCode.Created, new { html_url = "https://github.com/octo/privado/pull/7", number = 7 });
        }
        if (request.Method == HttpMethod.Get && path.Contains("/pulls?"))
            return Json(HttpStatusCode.OK, new[] { new { html_url = "https://github.com/octo/privado/pull/3", number = 3 } });
        if (request.Method == HttpMethod.Get && path.StartsWith("user/repos"))
            return Json(HttpStatusCode.OK, new[] { Repo("octo/publico", false), Repo("octo/privado", true) });
        if (request.Method == HttpMethod.Get && path.StartsWith("repos/"))
        {
            if (RepoMissing) return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
            var full = string.Join('/', path["repos/".Length..].Split('/').Take(2));
            return Json(HttpStatusCode.OK, Repo(full, full.EndsWith("/privado")));
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/keys"))
            return DeployKeyAlreadyInUse
                ? Json(HttpStatusCode.UnprocessableEntity, new { message = "Validation Failed", errors = new[] { new { message = "key is already in use" } } })
                : Json(HttpStatusCode.Created, new { id = 1 });
        return Json(HttpStatusCode.NotFound, new { message = "Not Found" });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

public class GitHubParsingTests
{
    [Theory]
    [InlineData("octo/repo", "octo/repo")]
    [InlineData("https://github.com/octo/repo", "octo/repo")]
    [InlineData("https://github.com/octo/repo.git", "octo/repo")]
    [InlineData("https://github.com/octo/repo/", "octo/repo")]
    [InlineData("git@github.com:octo/repo.git", "octo/repo")]
    [InlineData("ssh://git@github.com/octo/repo.git", "octo/repo")]
    [InlineData("  octo/my-repo_1.x  ", "octo/my-repo_1.x")]
    [InlineData("", null)]
    [InlineData("so-um-nome", null)]
    [InlineData("a/b/c", null)]
    [InlineData("octo/../etc", null)]
    [InlineData("octo/repo; rm -rf", null)]
    [InlineData("https://gitlab.com/octo/repo", null)]
    public void Parses_repository_references(string input, string? expected) => Assert.Equal(expected, GitHubClient.ParseFullName(input));
}

public sealed class GitHubImportApiTests : IAsyncLifetime
{
    private FakeGitHub _github = null!;
    private TestApiFactory _factory = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _github = new FakeGitHub();
        _factory = new TestApiFactory().WithServices(s =>
            s.AddHttpClient<GitHubClient>().ConfigurePrimaryHttpMessageHandler(() => _github));
        _http = await TestAuth.AuthenticatedClientAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> Import(object body) => _http.PostAsJsonAsync("/api/github/import", body);

    [Fact]
    public async Task Requires_the_login_and_rejects_mcp_access_tokens()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/github/repos", new { token = FakeGitHub.Token })).StatusCode);

        var created = await _http.PostAsJsonAsync("/api/tokens", new { name = "mcp" });
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var mcp = _factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mcp.PostAsJsonAsync("/api/github/import", new { repository = "octo/publico" })).StatusCode);
    }

    [Fact]
    public async Task Lists_the_repositories_of_the_token_without_echoing_it()
    {
        var response = await _http.PostAsJsonAsync("/api/github/repos", new { token = FakeGitHub.Token });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(FakeGitHub.Token, body);
        var repos = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Equal(["octo/publico", "octo/privado"], repos.EnumerateArray().Select(r => r.GetProperty("fullName").GetString()));
        Assert.True(repos[1].GetProperty("private").GetBoolean());
        Assert.Contains(_github.Requests, r => r.Path.StartsWith("user/repos") && r.Authorization == $"Bearer {FakeGitHub.Token}");
    }

    [Fact]
    public async Task Bad_or_missing_tokens_are_reported_clearly()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/github/repos", new { token = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/github/repos", new { token = "curto" })).StatusCode);

        var refused = await _http.PostAsJsonAsync("/api/github/repos", new { token = "ghp_token_errado_1234567890abcdef" });
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains("recusou o token", text);
        Assert.DoesNotContain("ghp_token_errado", text);
    }

    [Fact]
    public async Task Invalid_input_and_missing_repositories_are_reported()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Import(new { repository = "isto não é repo" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Import(new { repository = "octo/publico", token = "curto" })).StatusCode);

        _github.RepoMissing = true;
        var missing = await Import(new { repository = "octo/inexistente" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("não encontrado", await missing.Content.ReadAsStringAsync());
    }
}
