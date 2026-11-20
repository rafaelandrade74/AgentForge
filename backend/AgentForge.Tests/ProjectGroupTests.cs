using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Domain;
using AgentForge.Orchestrator.Workflow;

namespace AgentForge.Tests;

public class ProjectGroupApiTests : IAsyncLifetime
{
    private readonly TestApiFactory _factory = new();
    private HttpClient _http = null!;

    public async Task InitializeAsync() => _http = await TestAuth.AuthenticatedClientAsync(_factory);
    public Task DisposeAsync() { _factory.Dispose(); return Task.CompletedTask; }

    private async Task<Guid> CreateGroup(string name = "G1") =>
        (await (await _http.PostAsJsonAsync("/api/project-groups", new { name }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private async Task<Guid> CreateProject(string name = "P", Guid? groupId = null) =>
        (await (await _http.PostAsJsonAsync("/api/projects", new { name, repositoryUrl = "https://git/x.git", projectGroupId = groupId }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    [Fact]
    public async Task Group_crud_roundtrip()
    {
        var id = await CreateGroup("MeuGrupo");

        var get = await _http.GetFromJsonAsync<JsonElement>($"/api/project-groups/{id}");
        Assert.Equal("MeuGrupo", get.GetProperty("name").GetString());
        Assert.Equal(0, get.GetProperty("projectCount").GetInt32());

        var put = await _http.PutAsJsonAsync($"/api/project-groups/{id}", new { name = "Renomeado" });
        Assert.True(put.IsSuccessStatusCode);

        Assert.True((await _http.DeleteAsync($"/api/project-groups/{id}")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/project-groups/{id}")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_group_name_returns_conflict()
    {
        await CreateGroup("Unico");
        var res = await _http.PostAsJsonAsync("/api/project-groups", new { name = "Unico" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Project_in_group_shows_count()
    {
        var gid = await CreateGroup("ComProjeto");
        await CreateProject("P1", gid);
        await CreateProject("P2", gid);

        var get = await _http.GetFromJsonAsync<JsonElement>($"/api/project-groups/{gid}");
        Assert.Equal(2, get.GetProperty("projectCount").GetInt32());
    }

    [Fact]
    public async Task Demand_requires_project_or_group()
    {
        var res = await _http.PostAsJsonAsync("/api/demands", new { title = "D" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Demand_rejects_both_project_and_group()
    {
        var gid = await CreateGroup();
        var pid = await CreateProject("P", gid);
        var res = await _http.PostAsJsonAsync("/api/demands", new { title = "D", projectId = pid, projectGroupId = gid });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Group_demand_roundtrip()
    {
        var gid = await CreateGroup("GDemand");
        var res = await _http.PostAsJsonAsync("/api/demands", new { title = "D1", projectGroupId = gid });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var d = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(gid, d.GetProperty("projectGroupId").GetGuid());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("projectId").ValueKind);

        var list = await _http.GetFromJsonAsync<JsonElement>($"/api/demands?projectGroupId={gid}");
        Assert.Equal(1, list.GetArrayLength());
    }

    [Fact]
    public async Task Delete_group_ungroups_projects()
    {
        var gid = await CreateGroup("Efemero");
        var pid = await CreateProject("Filho", gid);

        await _http.DeleteAsync($"/api/project-groups/{gid}");

        var proj = await _http.GetFromJsonAsync<JsonElement>($"/api/projects/{pid}");
        Assert.Equal(JsonValueKind.Null, proj.GetProperty("projectGroupId").ValueKind);
    }
}

public class ParsePlanGroupTests
{
    [Fact]
    public void ParsePlan_with_project_field()
    {
        var json = """{"tasks":[{"key":"t1","project":"api","title":"T","description":"D","acceptanceCriteria":["ok"],"dependsOn":[],"estimate":1}]}""";
        var projects = new List<Project> { new() { Name = "api" }, new() { Name = "front" } };
        var result = OutputParsers.ParsePlan(json, projects);
        Assert.True(result.Ok);
        Assert.Equal("api", result.Value![0].Project);
    }

    [Fact]
    public void ParsePlan_group_rejects_missing_project()
    {
        var json = """{"tasks":[{"key":"t1","title":"T","description":"D","acceptanceCriteria":["ok"],"dependsOn":[],"estimate":1}]}""";
        var projects = new List<Project> { new() { Name = "api" } };
        var result = OutputParsers.ParsePlan(json, projects);
        Assert.False(result.Ok);
        Assert.Contains("project", result.Error!);
    }

    [Fact]
    public void ParsePlan_group_rejects_unknown_project()
    {
        var json = """{"tasks":[{"key":"t1","project":"desconhecido","title":"T","description":"D","acceptanceCriteria":["ok"],"dependsOn":[],"estimate":1}]}""";
        var projects = new List<Project> { new() { Name = "api" } };
        var result = OutputParsers.ParsePlan(json, projects);
        Assert.False(result.Ok);
        Assert.Contains("desconhecido", result.Error!);
    }

    [Fact]
    public void ParsePlan_without_group_ignores_project()
    {
        var json = """{"tasks":[{"key":"t1","title":"T","description":"D","acceptanceCriteria":["ok"],"dependsOn":[],"estimate":1}]}""";
        var result = OutputParsers.ParsePlan(json);
        Assert.True(result.Ok);
        Assert.Null(result.Value![0].Project);
    }
}
