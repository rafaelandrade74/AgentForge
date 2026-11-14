using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Environments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests;

public sealed class EnvironmentApiTests : IAsyncLifetime
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

    private object Body(string name = "maquina-a", string? url = null, string? token = null, string? root = null, string tags = "node20, dotnet8") =>
        new { name, url = url ?? "http://127.0.0.1:1", token = token ?? "token-do-host-agent-com-mais-de-24-caracteres", workspaceRoot = root ?? "/remote/a", tags };

    private async Task<JsonElement> CreateEnvironment(object? body = null)
    {
        var response = await _http.PostAsJsonAsync("/api/environments", body ?? Body());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Requires_the_login_and_rejects_mcp_access_tokens()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/environments")).StatusCode);

        var created = await _http.PostAsJsonAsync("/api/tokens", new { name = "mcp" });
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var mcp = _factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mcp.GetAsync("/api/environments")).StatusCode);
    }

    [Fact]
    public async Task The_token_is_stored_protected_and_never_returned()
    {
        const string token = "token-do-host-agent-com-mais-de-24-caracteres";
        var created = await CreateEnvironment();
        var id = created.GetProperty("id").GetGuid();

        Assert.True(created.GetProperty("hasToken").GetBoolean());
        var list = await (await _http.GetAsync("/api/environments")).Content.ReadAsStringAsync();
        var edited = await (await _http.PutAsJsonAsync($"/api/environments/{id}", Body(token: null, tags: "python3"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, created.GetRawText() + list + edited);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.ExecutionEnvironments.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.NotEmpty(stored.ProtectedToken);
        Assert.DoesNotContain(token, stored.ProtectedToken);
        Assert.Equal(token, _factory.Services.GetRequiredService<ISecretProtector>().Unprotect(stored.ProtectedToken));
        Assert.Equal("python3", stored.Tags);
    }

    [Fact]
    public async Task Validates_the_input_and_the_unique_name()
    {
        foreach (var bad in new object[]
        {
            Body(name: ""), Body(url: "ftp://x"), Body(url: "nao-e-url"), Body(url: "http://user:senha@host:8787"),
            Body(token: ""), Body(root: ""),
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/environments", bad)).StatusCode);

        await CreateEnvironment();
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsJsonAsync("/api/environments", Body())).StatusCode);
    }

    [Fact]
    public async Task A_project_needs_an_existing_environment_and_an_environment_in_use_cannot_be_deleted()
    {
        var bad = await _http.PostAsJsonAsync("/api/projects", new { name = "x", repositoryUrl = "https://h/r.git", environmentId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var id = (await CreateEnvironment()).GetProperty("id").GetGuid();
        var project = await _http.PostAsJsonAsync("/api/projects", new { name = "P", repositoryUrl = "https://h/r.git", environmentId = id });
        Assert.Equal(HttpStatusCode.Created, project.StatusCode);

        var refused = await _http.DeleteAsync($"/api/environments/{id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("1 projeto", await refused.Content.ReadAsStringAsync());

        var free = (await CreateEnvironment(Body("livre", root: "/remote/livre"))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/environments/{free}")).StatusCode);
    }

    [Fact]
    public async Task Lists_the_default_environment_and_how_many_projects_use_each()
    {
        var id = (await CreateEnvironment()).GetProperty("id").GetGuid();
        await _http.PostAsJsonAsync("/api/projects", new { name = "A", repositoryUrl = "https://h/a.git", environmentId = id });
        await _http.PostAsJsonAsync("/api/projects", new { name = "B", repositoryUrl = "https://h/b.git", environmentId = id });

        var list = await (await _http.GetAsync("/api/environments")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("local", list.GetProperty("default").GetProperty("mode").GetString());
        Assert.Equal(2, list.GetProperty("items")[0].GetProperty("projectCount").GetInt32());
    }
}
