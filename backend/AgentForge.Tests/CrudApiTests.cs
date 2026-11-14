using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests;

public class CrudApiTests : IAsyncLifetime
{
    private readonly TestApiFactory _factory = new();
    private HttpClient _http = null!;

    public async Task InitializeAsync() => _http = await TestAuth.AuthenticatedClientAsync(_factory);

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
    [InlineData("/api/approvals")]
    [InlineData("/api/agent-runs")]
    [InlineData("/api/review-findings")]
    [InlineData("/api/role-configs")]
    public async Task Endpoints_require_authentication(string url)
    {
        var res = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Approvals_lists_only_deliveries_awaiting_approval_from_every_project()
    {
        var p1 = await CreateAsync("/api/projects", new { name = "Alfa", repositoryUrl = "https://git/a.git" });
        var p2 = await CreateAsync("/api/projects", new { name = "Beta", repositoryUrl = "https://git/b.git" });
        var d1 = await CreateAsync("/api/demands", new { projectId = p1, title = "Demanda A", description = "d" });
        var d2 = await CreateAsync("/api/demands", new { projectId = p2, title = "Demanda B", description = "d" });
        var pendingA = await CreateAsync("/api/work-items", new { demandId = d1, title = "Entrega A", state = "AwaitingApproval" });
        var pendingB = await CreateAsync("/api/work-items", new { demandId = d2, title = "Entrega B", state = "AwaitingApproval" });
        await CreateAsync("/api/work-items", new { demandId = d1, title = "Em andamento", state = "InDevelopment" });
        await CreateAsync("/api/work-items", new { demandId = d2, title = "Pronta", state = "Done" });
        await CreateAsync("/api/work-items", new { demandId = d2, title = "Bloqueada", state = "Blocked" });

        var list = await _http.GetFromJsonAsync<JsonElement>("/api/approvals");

        var items = list.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var a = items.Single(i => i.GetProperty("id").GetGuid() == pendingA);
        Assert.Equal("Alfa", a.GetProperty("project").GetString());
        Assert.Equal("Demanda A", a.GetProperty("demand").GetString());
        Assert.Equal(p1, a.GetProperty("projectId").GetGuid());
        Assert.Equal("Beta", items.Single(i => i.GetProperty("id").GetGuid() == pendingB).GetProperty("project").GetString());
    }

    [Fact]
    public async Task Approvals_is_empty_when_nothing_waits()
    {
        Assert.Empty((await _http.GetFromJsonAsync<JsonElement>("/api/approvals")).EnumerateArray());
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
    public async Task WorkItems_can_be_listed_by_project()
    {
        var p1 = await NewProject();
        var p2 = await NewProject();
        var d1 = await CreateAsync("/api/demands", new { projectId = p1, title = "D1" });
        var d2 = await CreateAsync("/api/demands", new { projectId = p2, title = "D2" });
        await CreateAsync("/api/work-items", new { demandId = d1, title = "do projeto 1" });
        await CreateAsync("/api/work-items", new { demandId = d2, title = "do projeto 2" });

        var list = await _http.GetFromJsonAsync<JsonElement>($"/api/work-items?projectId={p1}");

        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("do projeto 1", list[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Agent_run_list_is_lightweight_and_full_run_is_by_id()
    {
        var project = await NewProject();
        var demand = await CreateAsync("/api/demands", new { projectId = project, title = "D" });
        Guid runId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentForge.Infrastructure.Persistence.AppDbContext>();
            var run = new AgentForge.Domain.AgentRun { DemandId = demand, Role = AgentForge.Domain.AgentRole.ScrumMaster, Prompt = "PROMPT", Output = "SAIDA", CostUsd = 0.5m };
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        var list = await _http.GetFromJsonAsync<JsonElement>($"/api/agent-runs?demandId={demand}");
        var full = await _http.GetFromJsonAsync<JsonElement>($"/api/agent-runs/{runId}");

        Assert.Equal(1, list.GetArrayLength());
        Assert.False(list[0].TryGetProperty("prompt", out _));
        Assert.False(list[0].TryGetProperty("output", out _));
        Assert.Equal(0.5m, list[0].GetProperty("costUsd").GetDecimal());
        Assert.Equal("SAIDA", full.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Work_item_diff_endpoint_returns_text_or_404()
    {
        var project = await NewProject();
        var demand = await CreateAsync("/api/demands", new { projectId = project, title = "D" });
        var item = await CreateAsync("/api/work-items", new { demandId = demand, title = "A" });

        var ok = await _http.GetAsync($"/api/work-items/{item}/diff");
        var missing = await _http.GetAsync($"/api/work-items/{Guid.NewGuid()}/diff");

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("text/plain", ok.Content.Headers.ContentType!.MediaType);
        Assert.Equal("", await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_project_removes_the_whole_graph_even_with_dependencies_runs_and_findings()
    {
        var project = await NewProject();
        var demand = await CreateAsync("/api/demands", new { projectId = project, title = "D" });
        var a = await CreateAsync("/api/work-items", new { demandId = demand, title = "A" });
        var b = await CreateAsync("/api/work-items", new { demandId = demand, title = "B", dependsOnIds = new[] { a } });
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AgentForge.Infrastructure.Persistence.AppDbContext>();
            var run = new AgentForge.Domain.AgentRun { DemandId = demand, WorkItemId = b, Role = AgentForge.Domain.AgentRole.Dev, Prompt = "p" };
            db.AgentRuns.Add(run);
            db.ReviewFindings.Add(new AgentForge.Domain.ReviewFinding { WorkItemId = b, AgentRunId = run.Id, Role = AgentForge.Domain.AgentRole.TechLead, Message = "m" });
            await db.SaveChangesAsync();
        }

        var delete = await _http.DeleteAsync($"/api/projects/{project}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/work-items/{a}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/demands/{demand}")).StatusCode);
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
    public async Task Swagger_can_be_turned_off_by_configuration()
    {
        await using var off = _factory.WithWebHostBuilder(b => b.UseSetting("Swagger:Enabled", "false"));
        var client = off.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Swagger_documents_the_endpoints()
    {
        var doc = await _factory.CreateClient().GetStringAsync("/swagger/v1/swagger.json");
        foreach (var path in new[] { "/api/projects", "/api/demands", "/api/work-items", "/api/agent-runs", "/api/review-findings", "/api/role-configs", "/api/auth/login" })
            Assert.Contains($"\"{path}\"", doc);
    }
}
