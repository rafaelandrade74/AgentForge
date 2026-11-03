using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentForge.Tests;

[Collection(PostgresCollection.Name)]
public class CrudApiTests : IAsyncLifetime
{
    private readonly ApiFactory _factory;
    private HttpClient _http = null!;

    public CrudApiTests(PostgresFixture pg) => _factory = new ApiFactory(pg.NewConnectionString());

    public async Task InitializeAsync()
    {
        _http = _factory.CreateClient();
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> CreateAsync(string url, object body)
    {
        var res = await _http.PostAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<Guid> NewProject() => CreateAsync("/api/projects", new { name = "P", repositoryUrl = "https://git/x.git" });

    [Theory]
    [InlineData("/api/projects")]
    [InlineData("/api/demands")]
    [InlineData("/api/work-items")]
    [InlineData("/api/agent-runs")]
    [InlineData("/api/review-findings")]
    [InlineData("/api/role-configs")]
    public async Task Endpoints_require_authentication(string url)
    {
        var res = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Project_crud_roundtrip()
    {
        var id = await NewProject();

        var put = await _http.PutAsJsonAsync($"/api/projects/{id}", new { name = "Novo", repositoryUrl = "https://git/y.git", baseBranch = "develop", autoMode = true });
        var got = await _http.GetFromJsonAsync<JsonElement>($"/api/projects/{id}");
        var del = await _http.DeleteAsync($"/api/projects/{id}");
        var after = await _http.GetAsync($"/api/projects/{id}");

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("develop", got.GetProperty("baseBranch").GetString());
        Assert.True(got.GetProperty("autoMode").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Project_without_name_returns_400()
    {
        var res = await _http.PostAsJsonAsync("/api/projects", new { name = "", repositoryUrl = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Demand_for_unknown_project_returns_400()
    {
        var res = await _http.PostAsJsonAsync("/api/demands", new { projectId = Guid.NewGuid(), title = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task WorkItem_dependencies_and_enum_as_string()
    {
        var project = await NewProject();
        var demand = await CreateAsync("/api/demands", new { projectId = project, title = "D" });
        var a = await CreateAsync("/api/work-items", new { demandId = demand, title = "A" });
        var b = await CreateAsync("/api/work-items", new { demandId = demand, title = "B", state = "Todo", dependsOnIds = new[] { a } });

        var got = await _http.GetFromJsonAsync<JsonElement>($"/api/work-items/{b}");
        var self = await _http.PutAsJsonAsync($"/api/work-items/{b}", new { demandId = demand, title = "B", dependsOnIds = new[] { b } });
        var delA = await _http.DeleteAsync($"/api/work-items/{a}");
        var afterDel = await _http.GetFromJsonAsync<JsonElement>($"/api/work-items/{b}");

        Assert.Equal("Todo", got.GetProperty("state").GetString());
        Assert.Equal(a, got.GetProperty("dependsOnIds")[0].GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, delA.StatusCode);
        Assert.Equal(0, afterDel.GetProperty("dependsOnIds").GetArrayLength());
    }

    [Fact]
    public async Task Review_finding_crud()
    {
        var project = await NewProject();
        var demand = await CreateAsync("/api/demands", new { projectId = project, title = "D" });
        var item = await CreateAsync("/api/work-items", new { demandId = demand, title = "A" });

        var id = await CreateAsync("/api/review-findings",
            new { workItemId = item, role = "Security", severity = "High", message = "SQL injection", file = "a.cs", line = 10 });
        var list = await _http.GetFromJsonAsync<JsonElement>($"/api/review-findings?workItemId={item}");

        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("High", list[0].GetProperty("severity").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/review-findings/{id}")).StatusCode);
    }

    [Fact]
    public async Task Role_configs_are_seeded_and_unique_per_role()
    {
        var list = await _http.GetFromJsonAsync<JsonElement>("/api/role-configs");
        Assert.Equal(5, list.GetArrayLength());

        var dup = await _http.PostAsJsonAsync("/api/role-configs",
            new { role = "Dev", adapterName = "claude-code", systemPrompt = "", allowedTools = "", model = "", maxTurns = 5 });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Swagger_documents_the_endpoints()
    {
        var doc = await _factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");
        foreach (var path in new[] { "/api/projects", "/api/demands", "/api/work-items", "/api/agent-runs", "/api/review-findings", "/api/role-configs", "/api/auth/login" })
            Assert.Contains($"\"{path}\"", doc);
    }
}
