using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentForge.Tests;

/// <summary>API do ambiente por papel e da concorrência/carga por ambiente (fase 13).</summary>
[Collection(PostgresCollection.Name)]
public sealed class EnvironmentRolesApiTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-roles-" + Guid.NewGuid().ToString("N"));
    private Factory _factory = null!;
    private HttpClient _http = null!;

    private sealed class Factory(string cs, string temp) : ApiFactory(cs)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("DataProtection:KeysDir", Path.Combine(temp, "keys"));
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        _factory = new Factory(pg.NewConnectionString(), _temp);
        _http = _factory.CreateClient();
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<HttpResponseMessage> PostEnvironment(string name, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name, ["url"] = "http://127.0.0.1:1", ["token"] = "token-com-mais-de-24-caracteres-xx", ["workspaceRoot"] = "/x" };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        return await _http.PostAsJsonAsync("/api/environments", body);
    }

    private async Task<Guid> Environment(string name, int? max = null)
    {
        var response = await PostEnvironment(name, max is null ? null : new { maxConcurrency = max });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> PostProject(object body) => _http.PostAsJsonAsync("/api/projects", body);

    [Fact]
    public async Task The_concurrency_limit_defaults_to_one_is_validated_and_can_be_changed()
    {
        var created = await (await PostEnvironment("a")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, created.GetProperty("maxConcurrency").GetInt32());

        foreach (var bad in new[] { 0, -1, 33 })
            Assert.Equal(HttpStatusCode.BadRequest, (await PostEnvironment("b" + bad, new { maxConcurrency = bad })).StatusCode);

        var id = created.GetProperty("id").GetGuid();
        var edited = await _http.PutAsJsonAsync($"/api/environments/{id}", new { name = "a", url = "http://127.0.0.1:1", workspaceRoot = "/x", maxConcurrency = 4 });
        Assert.Equal(4, (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("maxConcurrency").GetInt32());

        // editar sem informar o limite não o altera
        var again = await _http.PutAsJsonAsync($"/api/environments/{id}", new { name = "a", url = "http://127.0.0.1:1", workspaceRoot = "/x", tags = "node" });
        Assert.Equal(4, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("maxConcurrency").GetInt32());
    }

    [Fact]
    public async Task Only_tech_lead_and_security_can_move_to_another_environment()
    {
        var light = await Environment("leve");

        foreach (var role in new[] { "Dev", "Qa", "ScrumMaster", "Human", "Inexistente" })
        {
            var refused = await PostProject(new { name = "P" + role, repositoryUrl = "https://h/r.git", roleEnvironments = new Dictionary<string, Guid?> { [role] = light } });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("TechLead e Security", await refused.Content.ReadAsStringAsync());
        }
        var ghost = await PostProject(new { name = "Pghost", repositoryUrl = "https://h/r.git", roleEnvironments = new Dictionary<string, Guid?> { ["TechLead"] = Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, ghost.StatusCode);
    }

    [Fact]
    public async Task Role_environments_are_saved_returned_kept_when_omitted_and_cleared_when_empty()
    {
        var light = await Environment("leve");
        var created = await PostProject(new { name = "P", repositoryUrl = "https://h/r.git", roleEnvironments = new Dictionary<string, Guid?> { ["TechLead"] = light, ["Security"] = light } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = project.GetProperty("id").GetGuid();
        Assert.Equal(light, project.GetProperty("roleEnvironments").GetProperty("TechLead").GetGuid());
        Assert.Equal(light, project.GetProperty("roleEnvironments").GetProperty("Security").GetGuid());

        // omitido = mantém
        var kept = await (await _http.PutAsJsonAsync($"/api/projects/{id}", new { name = "P2", repositoryUrl = "https://h/r.git" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, kept.GetProperty("roleEnvironments").EnumerateObject().Count());

        // só um papel (o outro volta ao ambiente do projeto)
        var one = await (await _http.PutAsJsonAsync($"/api/projects/{id}", new { name = "P2", repositoryUrl = "https://h/r.git", roleEnvironments = new Dictionary<string, Guid?> { ["Security"] = light, ["TechLead"] = null } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["Security"], one.GetProperty("roleEnvironments").EnumerateObject().Select(p => p.Name));

        // lista e leitura individual trazem os papéis
        var list = await _http.GetFromJsonAsync<JsonElement>("/api/projects");
        Assert.Equal(1, list.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == id).GetProperty("roleEnvironments").EnumerateObject().Count());
        var single = await _http.GetFromJsonAsync<JsonElement>($"/api/projects/{id}");
        Assert.True(single.GetProperty("roleEnvironments").TryGetProperty("Security", out _));

        // vazio = limpa
        var cleared = await (await _http.PutAsJsonAsync($"/api/projects/{id}", new { name = "P2", repositoryUrl = "https://h/r.git", roleEnvironments = new Dictionary<string, Guid?>() })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(cleared.GetProperty("roleEnvironments").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task An_environment_that_runs_a_role_of_a_project_cannot_be_deleted()
    {
        var light = await Environment("leve");
        var project = await (await PostProject(new { name = "P", repositoryUrl = "https://h/r.git", roleEnvironments = new Dictionary<string, Guid?> { ["TechLead"] = light } })).Content.ReadFromJsonAsync<JsonElement>();

        var refused = await _http.DeleteAsync($"/api/environments/{light}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Tech Lead/Security", await refused.Content.ReadAsStringAsync());

        // apagar o projeto libera o ambiente (os vínculos saem junto)
        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/projects/{project.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/environments/{light}")).StatusCode);
    }

    [Fact]
    public async Task The_load_panel_lists_every_environment_requires_login_and_rejects_mcp_tokens()
    {
        var a = await Environment("a", 3);
        await PostProject(new { name = "P", repositoryUrl = "https://h/r.git", environmentId = a });

        var report = await _http.GetFromJsonAsync<JsonElement>("/api/environments/load");

        var rows = report.GetProperty("environments").EnumerateArray().ToList();
        Assert.Equal("default", rows[0].GetProperty("kind").GetString());
        var mine = rows.Single(r => r.GetProperty("id").ValueKind != JsonValueKind.Null && r.GetProperty("id").GetGuid() == a);
        Assert.Equal(3, mine.GetProperty("maxConcurrency").GetInt32());
        Assert.Equal(1, mine.GetProperty("projectCount").GetInt32());
        Assert.Equal(0, mine.GetProperty("runningTasks").GetInt32());
        Assert.Equal(0, report.GetProperty("waiting").GetArrayLength());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/environments/load")).StatusCode);
        var token = await (await _http.PostAsJsonAsync("/api/tokens", new { name = "mcp" })).Content.ReadFromJsonAsync<JsonElement>();
        var mcp = _factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await mcp.GetAsync("/api/environments/load")).StatusCode);
    }
}
