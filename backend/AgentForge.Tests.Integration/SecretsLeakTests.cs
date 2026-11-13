using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Execution;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests.Integration;

public class SecretsTests
{
    [Theory]
    [InlineData("https://ghp_abc123@github.com/org/repo.git", "https://***@github.com/org/repo.git")]
    [InlineData("https://user:s3nha@git.exemplo.com/x.git", "https://***@git.exemplo.com/x.git")]
    [InlineData("HTTP://tok@host/x", "HTTP://***@host/x")]
    [InlineData("https://github.com/org/repo.git", "https://github.com/org/repo.git")]
    [InlineData("git@github.com:org/repo.git", "git@github.com:org/repo.git")]
    [InlineData("ssh://git@github.com/org/repo.git", "ssh://git@github.com/org/repo.git")]
    [InlineData("fatal: unable to access 'https://tok123@github.com/o/r.git/': 403", "fatal: unable to access 'https://***@github.com/o/r.git/': 403")]
    [InlineData("", "")]
    public void Masks_credentials_embedded_in_http_urls(string input, string expected) => Assert.Equal(expected, Secrets.MaskUrls(input));

    [Fact]
    public void Git_errors_never_repeat_the_token_of_the_url()
    {
        var ex = new GitException("git clone falhou (128): fatal: repository 'https://ghp_segredo@github.com/o/r.git/' not found", 128);

        Assert.DoesNotContain("ghp_segredo", ex.Message);
        Assert.Contains("https://***@github.com", ex.Message);
    }

    [Fact]
    public void Child_processes_lose_the_application_secrets_but_keep_the_claude_credentials()
    {
        var env = new Dictionary<string, string?>
        {
            ["Auth__JwtSecret"] = "x", ["Auth__AdminPassword"] = "x", ["ConnectionStrings__Default"] = "Host=db;Password=x",
            ["Agents__HostAgent__Token"] = "x", ["HostAgent__Token"] = "x", ["ADMIN_PASSWORD"] = "x", ["JWT_SECRET"] = "x",
            ["POSTGRES_PASSWORD"] = "x", ["HOST_AGENT_TOKEN"] = "x", ["DATABASE_URL"] = "x", ["auth__issuer"] = "x",
            ["ANTHROPIC_API_KEY"] = "sk-ant-x", ["CLAUDE_CONFIG_DIR"] = "/home/app/.claude", ["PATH"] = "/usr/bin", ["HOME"] = "/home/app",
        };

        Secrets.ScrubEnvironment(env);

        Assert.Equal(["ANTHROPIC_API_KEY", "CLAUDE_CONFIG_DIR", "HOME", "PATH"], env.Keys.Order());
    }

    [Fact]
    public void The_launcher_applies_the_scrub_to_the_real_process_environment()
    {
        var previous = Environment.GetEnvironmentVariable("Auth__JwtSecret");
        Environment.SetEnvironmentVariable("Auth__JwtSecret", "segredo-que-nao-pode-vazar");
        try
        {
            var psi = LocalProcessLauncher.StartInfo("git", ["--version"], Path.GetTempPath());
            Assert.False(psi.Environment.ContainsKey("Auth__JwtSecret"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("Auth__JwtSecret", previous);
        }
    }

    [SkippableLinuxFact]
    public async Task A_real_child_process_cannot_see_the_secrets_with_env()
    {
        Environment.SetEnvironmentVariable("JWT_SECRET", "segredo-que-nao-pode-vazar");
        try
        {
            var result = await new LocalProcessLauncher().RunAsync(new LaunchSpec("env", [], Path.GetTempPath()), TimeSpan.FromSeconds(10), default);

            Assert.True(result.Started);
            Assert.DoesNotContain("segredo-que-nao-pode-vazar", result.Stdout);
            Assert.Contains("PATH=", result.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JWT_SECRET", null);
        }
    }
}

public sealed class SkippableLinuxFactAttribute : FactAttribute
{
    public SkippableLinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "usa o comando env (Linux)";
    }
}

public sealed class ProjectUrlCredentialTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string WithToken = "https://ghp_segredo123@github.com/org/repo.git";
    private const string Masked = "https://***@github.com/org/repo.git";
    private readonly ApiFactory _factory = new(pg.NewConnectionString());
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _http = _factory.CreateClient();
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> StoredUrlAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AgentForge.Infrastructure.Persistence.AppDbContext>().Projects.FindAsync(id))!.RepositoryUrl;
    }

    private async Task<Guid> CreateWithTokenAsync()
    {
        var created = await _http.PostAsJsonAsync("/api/projects", new { name = "P", repositoryUrl = WithToken });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var text = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ghp_segredo123", text);
        return JsonSerializer.Deserialize<JsonElement>(text).GetProperty("id").GetGuid();
    }

    [IntegrationFact]
    public async Task The_api_never_returns_the_token_of_the_url_but_the_database_keeps_it()
    {
        var id = await CreateWithTokenAsync();

        var one = await _http.GetStringAsync($"/api/projects/{id}");
        var all = await _http.GetStringAsync("/api/projects");

        Assert.DoesNotContain("ghp_segredo123", one + all);
        Assert.Contains(Masked, one);
        Assert.Equal(WithToken, await StoredUrlAsync(id));
    }

    [IntegrationFact]
    public async Task Saving_the_masked_url_back_keeps_the_stored_credential()
    {
        var id = await CreateWithTokenAsync();

        var put = await _http.PutAsJsonAsync($"/api/projects/{id}", new { name = "Renomeado", repositoryUrl = Masked, baseBranch = "dev" });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(WithToken, await StoredUrlAsync(id));
        Assert.Equal("Renomeado", (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
    }

    [IntegrationFact]
    public async Task A_new_url_replaces_it_and_a_masked_url_that_does_not_match_is_refused()
    {
        var id = await CreateWithTokenAsync();

        var other = await _http.PutAsJsonAsync($"/api/projects/{id}", new { name = "P", repositoryUrl = "https://***@gitlab.com/x/y.git" });
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Equal(WithToken, await StoredUrlAsync(id));

        var fresh = await _http.PostAsJsonAsync("/api/projects", new { name = "N", repositoryUrl = Masked });
        Assert.Equal(HttpStatusCode.BadRequest, fresh.StatusCode);

        var replaced = await _http.PutAsJsonAsync($"/api/projects/{id}", new { name = "P", repositoryUrl = "git@github.com:org/repo.git" });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal("git@github.com:org/repo.git", await StoredUrlAsync(id));
    }
}

public sealed class LoginRateLimitTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [IntegrationFact]
    public async Task Too_many_login_attempts_from_the_same_address_get_429_even_with_the_right_password()
    {
        await using var factory = new ApiFactory(pg.NewConnectionString()).WithWebHostBuilder(b => b.UseSetting("Auth:LoginAttemptsPerMinute", "3"));
        var http = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "errada" })).StatusCode);

        var blocked = await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [IntegrationFact]
    public async Task The_limit_can_be_turned_off_and_does_not_touch_other_routes()
    {
        await using var factory = new ApiFactory(pg.NewConnectionString()).WithWebHostBuilder(b => b.UseSetting("Auth:LoginAttemptsPerMinute", "0"));
        var http = factory.CreateClient();

        for (var i = 0; i < 15; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "errada" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
    }
}
