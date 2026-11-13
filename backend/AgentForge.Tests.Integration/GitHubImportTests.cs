using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentForge.Api.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace AgentForge.Tests.Integration;

/// <summary>GitHub simulado: responde às rotas usadas e registra o que recebeu (inclusive o cabeçalho de autorização).</summary>
public sealed class FakeGitHub(string cloneUrl) : HttpMessageHandler
{
    public const string Token = "ghp_token_de_teste_1234567890abcdef";
    public record Seen(string Method, string Path, string? Authorization, string Body);
    public ConcurrentQueue<Seen> Requests { get; } = new();
    public bool DeployKeyAlreadyInUse { get; set; }
    public bool RepoMissing { get; set; }
    /// <summary>Criar o PR responde "já existe um PR aberto para este branch" (e a listagem devolve o existente).</summary>
    public bool PullAlreadyExists { get; set; }
    /// <summary>Criar o PR responde 422 (ex.: a branch de destino não existe).</summary>
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

public sealed class GitHubImportTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-gh-" + Guid.NewGuid().ToString("N"));
    private string _origin = "";
    private FakeGitHub _github = null!;
    private ApiFactory _factory = null!;
    private HttpClient _http = null!;

    private sealed class GhFactory(string cs, string temp, FakeGitHub github) : ApiFactory(cs)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Workspaces:Root", Path.Combine(temp, "repos"));
            builder.UseSetting("Git:SshDir", Path.Combine(temp, "ssh"));
            builder.ConfigureServices(s => s.AddHttpClient<GitHubClient>().ConfigurePrimaryHttpMessageHandler(() => github));
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        _origin = await GitOrigin.CreateAsync(_temp);
        _github = new FakeGitHub(_origin);
        _factory = new GhFactory(pg.NewConnectionString(), _temp, _github);
        _http = _factory.CreateClient();
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> Import(object body) => _http.PostAsJsonAsync("/api/github/import", body);

    [IntegrationFact]
    public async Task Requires_the_login_and_rejects_mcp_access_tokens()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/github/repos", new { token = FakeGitHub.Token })).StatusCode);

        var created = await _http.PostAsJsonAsync("/api/tokens", new { name = "mcp" });
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var mcp = _factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mcp.PostAsJsonAsync("/api/github/import", new { repository = "octo/publico" })).StatusCode);
    }

    [IntegrationFact]
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

    [IntegrationFact]
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

    [IntegrationFact]
    public async Task Importing_a_public_repo_creates_the_project_and_already_clones_it()
    {
        var response = await Import(new { repository = "https://github.com/octo/publico.git", buildCommand = "dotnet build", testCommand = "dotnet test" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var project = body.GetProperty("project");
        Assert.Equal("publico", project.GetProperty("name").GetString());
        Assert.Equal("main", project.GetProperty("baseBranch").GetString());
        Assert.Equal(_origin, project.GetProperty("repositoryUrl").GetString());
        Assert.Equal("dotnet test", project.GetProperty("testCommand").GetString());
        Assert.True(body.GetProperty("cloned").GetBoolean());
        Assert.False(body.GetProperty("deployKeyAdded").GetBoolean());

        var id = project.GetProperty("id").GetGuid();
        Assert.True(Directory.Exists(Path.Combine(_temp, "repos", id.ToString(), "repo", ".git")), "o clone já deve existir");
        Assert.DoesNotContain(_github.Requests, r => r.Path.EndsWith("/keys"));
        Assert.All(_github.Requests, r => Assert.Null(r.Authorization));   // sem token, nada é enviado
    }

    [SshFact]
    public async Task Importing_a_private_repo_generates_the_key_registers_a_read_only_deploy_key_and_clones()
    {
        var response = await Import(new { repository = "octo/privado", token = FakeGitHub.Token, addDeployKey = true });
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(FakeGitHub.Token, text);
        Assert.True(body.GetProperty("deployKeyAdded").GetBoolean());
        Assert.Contains("deploy key somente leitura", string.Join(' ', body.GetProperty("notes").EnumerateArray().Select(n => n.GetString())));

        var posted = _github.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/keys"));
        Assert.Equal("repos/octo/privado/keys", posted.Path);
        Assert.Equal($"Bearer {FakeGitHub.Token}", posted.Authorization);
        var sent = JsonSerializer.Deserialize<JsonElement>(posted.Body);
        Assert.True(sent.GetProperty("read_only").GetBoolean());
        Assert.StartsWith("ssh-ed25519 ", sent.GetProperty("key").GetString());
        Assert.Equal(sent.GetProperty("key").GetString(), (await File.ReadAllTextAsync(Path.Combine(_temp, "ssh", "id_ed25519.pub"))).Trim());
        Assert.True(Directory.Exists(Path.Combine(_temp, "repos", body.GetProperty("project").GetProperty("id").GetGuid().ToString(), "repo", ".git")));
    }

    [SshFact]
    public async Task A_deploy_key_already_in_use_does_not_stop_the_import()
    {
        _github.DeployKeyAlreadyInUse = true;

        var response = await Import(new { repository = "octo/privado", token = FakeGitHub.Token });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("já estava registrada", string.Join(' ', body.GetProperty("notes").EnumerateArray().Select(n => n.GetString())));
    }

    [SshFact]
    public async Task Without_adding_the_key_automatically_it_tells_the_person_to_authorize_it()
    {
        var response = await Import(new { repository = "octo/privado", token = FakeGitHub.Token, addDeployKey = false });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("deployKeyAdded").GetBoolean());
        Assert.Contains("Autorize a chave pública", string.Join(' ', body.GetProperty("notes").EnumerateArray().Select(n => n.GetString())));
        Assert.DoesNotContain(_github.Requests, r => r.Path.EndsWith("/keys"));
    }

    [IntegrationFact]
    public async Task Importing_the_same_repository_twice_is_refused()
    {
        Assert.Equal(HttpStatusCode.OK, (await Import(new { repository = "octo/publico" })).StatusCode);

        var again = await Import(new { repository = "octo/publico" });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("já está cadastrado", await again.Content.ReadAsStringAsync());
    }

    [IntegrationFact]
    public async Task Failed_clone_rolls_back_the_project()
    {
        await using var broken = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient<GitHubClient>().ConfigurePrimaryHttpMessageHandler(() => new FakeGitHub(Path.Combine(_temp, "nao-existe.git")))));
        var client = broken.CreateClient();
        client.DefaultRequestHeaders.Authorization = _http.DefaultRequestHeaders.Authorization;

        var response = await client.PostAsJsonAsync("/api/github/import", new { repository = "octo/publico" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("não foi possível clonar", await response.Content.ReadAsStringAsync());
        Assert.Empty((await _http.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray());
    }

    [IntegrationFact]
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
