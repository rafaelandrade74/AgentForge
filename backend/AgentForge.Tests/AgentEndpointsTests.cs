using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentForge.Tests;

public sealed class AgentEndpointsTests : IAsyncLifetime
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

    [Fact]
    public async Task Agent_endpoints_require_authentication()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/agents/claude-code/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/agents/claude-code/test", null)).StatusCode);
    }

    [Fact]
    public async Task Lists_claude_code_with_the_roles_that_use_it()
    {
        var agents = await _http.GetFromJsonAsync<JsonElement>("/api/agents");

        var claude = agents.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "claude-code");
        Assert.Equal("Claude Code", claude.GetProperty("displayName").GetString());
        Assert.True(claude.GetProperty("supportsLogin").GetBoolean());
        Assert.Equal(5, claude.GetProperty("usedByRoles").GetArrayLength());
    }

    [Fact]
    public async Task Environment_tells_where_the_clis_run()
    {
        var env = await _http.GetFromJsonAsync<JsonElement>("/api/agents/environment");

        Assert.Equal("local", env.GetProperty("mode").GetString());
        Assert.NotEmpty(env.GetProperty("description").GetString()!);
    }

    [Fact]
    public async Task Api_serves_the_built_frontend_when_configured_and_keeps_api_routes_404()
    {
        var dir = Path.Combine(Path.GetTempPath(), "af-spa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "index.html"), "<html>SPA-AgentForge</html>");
        await File.WriteAllTextAsync(Path.Combine(dir, "app.js"), "console.log(1)");
        try
        {
            await using var spa = _factory.WithWebHostBuilder(b => b.UseSetting("Frontend:Path", dir));
            var client = spa.CreateClient();

            Assert.Contains("SPA-AgentForge", await client.GetStringAsync("/"));
            Assert.Contains("SPA-AgentForge", await client.GetStringAsync("/projects/abc/demands/new"));
            Assert.Equal("console.log(1)", await client.GetStringAsync("/app.js"));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/nao-existe")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/agents")).StatusCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_agent_returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/agents/nao-existe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/agents/nao-existe/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/agents/nao-existe/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/agents/claude-code/login/sessao-inexistente")).StatusCode);
    }

    [Fact]
    public async Task Role_config_only_accepts_registered_agents()
    {
        var configs = await _http.GetFromJsonAsync<JsonElement>("/api/role-configs");
        var dev = configs.EnumerateArray().First(c => c.GetProperty("role").GetString() == "Dev");
        var id = dev.GetProperty("id").GetGuid();
        object Body(string adapter) => new
        {
            role = "Dev", adapterName = adapter, systemPrompt = "x", allowedTools = "Read", model = "", maxTurns = 5,
        };

        var bad = await _http.PutAsJsonAsync($"/api/role-configs/{id}", Body("agente-inexistente"));
        var good = await _http.PutAsJsonAsync($"/api/role-configs/{id}", Body("claude-code"));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }
}
