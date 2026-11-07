using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests;

[Collection(PostgresCollection.Name)]
public sealed class McpTests(PostgresFixture pg) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _jwt = null!;       // logado (JWT)
    private HttpClient _mcp = null!;       // só o token de acesso
    private string _token = "";
    private Guid _tokenId;
    private int _rpcId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(pg.NewConnectionString());
        _jwt = _factory.CreateClient();
        var login = await _jwt.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        var jwt = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        _jwt.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var created = await _jwt.PostAsJsonAsync("/api/tokens", new { name = "teste" });
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        _token = body.GetProperty("token").GetString()!;
        _tokenId = body.GetProperty("id").GetGuid();
        _mcp = _factory.CreateClient();
        _mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------- helpers

    private static StringContent Rpc(object message) => new(JsonSerializer.Serialize(message), Encoding.UTF8, "application/json");

    private async Task<JsonElement> CallAsync(string method, object? @params = null, HttpClient? client = null)
    {
        var response = await (client ?? _mcp).PostAsync("/mcp", Rpc(new { jsonrpc = "2.0", id = ++_rpcId, method, @params }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Chama uma ferramenta e devolve (isError, texto).</summary>
    private async Task<(bool IsError, string Text)> ToolAsync(string name, object? arguments = null)
    {
        var reply = await CallAsync("tools/call", new { name, arguments = arguments ?? new { } });
        var result = reply.GetProperty("result");
        return (result.GetProperty("isError").GetBoolean(), result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    private async Task<JsonElement> ToolJsonAsync(string name, object? arguments = null)
    {
        var (isError, text) = await ToolAsync(name, arguments);
        Assert.False(isError, text);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private async Task<(Guid Project, Guid Demand, Guid Item)> SeedAsync(WorkItemState state = WorkItemState.Backlog)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new Project { Name = "P", RepositoryUrl = "x" };
        var demand = new Demand { ProjectId = project.Id, Title = "D", Status = DemandStatus.InProgress };
        var item = new WorkItem { DemandId = demand.Id, Title = "Tarefa", State = state, AcceptanceCriteria = "- critério" };
        db.AddRange(project, demand, item);
        await db.SaveChangesAsync();
        return (project.Id, demand.Id, item.Id);
    }

    // ---------------------------------------------------------------------------- autenticação

    [Fact]
    public async Task Endpoint_requires_a_valid_token()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/mcp", Rpc(new { jsonrpc = "2.0", id = 1, method = "ping" }))).StatusCode);

        var bogus = _factory.CreateClient();
        bogus.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "afk_nao-existe");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bogus.PostAsync("/mcp", Rpc(new { jsonrpc = "2.0", id = 1, method = "ping" }))).StatusCode);

        // o JWT do login também vale no /mcp; e o token de acesso funciona
        Assert.Equal(HttpStatusCode.OK, (await _jwt.PostAsync("/mcp", Rpc(new { jsonrpc = "2.0", id = 1, method = "ping" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _mcp.PostAsync("/mcp", Rpc(new { jsonrpc = "2.0", id = 1, method = "ping" }))).StatusCode);
    }

    [Fact]
    public async Task Access_token_only_works_on_mcp_and_cannot_manage_tokens()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _mcp.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _mcp.GetAsync("/api/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _mcp.PostAsJsonAsync("/api/tokens", new { name = "outro" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _mcp.GetAsync("/api/agents")).StatusCode);
    }

    [Fact]
    public async Task Tokens_show_the_secret_once_and_can_be_revoked()
    {
        var list = await _jwt.GetFromJsonAsync<JsonElement>("/api/tokens");
        var entry = list.EnumerateArray().Single();
        Assert.Equal("teste", entry.GetProperty("name").GetString());
        Assert.StartsWith("afk_", entry.GetProperty("prefix").GetString());
        Assert.False(entry.TryGetProperty("token", out _));
        Assert.DoesNotContain(_token, await _jwt.GetStringAsync("/api/tokens"));

        // o hash, não o valor, fica no banco
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = db.ApiTokens.Single();
            Assert.NotEqual(_token, stored.TokenHash);
            Assert.Equal(64, stored.TokenHash.Length);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await _jwt.PostAsJsonAsync("/api/tokens", new { name = " " })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _jwt.DeleteAsync($"/api/tokens/{_tokenId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _mcp.PostAsync("/mcp", Rpc(new { jsonrpc = "2.0", id = 1, method = "ping" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _jwt.DeleteAsync($"/api/tokens/{_tokenId}")).StatusCode);
        var after = (await _jwt.GetFromJsonAsync<JsonElement>("/api/tokens")).EnumerateArray().Single();
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("revokedAt").ValueKind);
    }

    // ---------------------------------------------------------------------------- protocolo

    [Fact]
    public async Task Initialize_negotiates_the_protocol_version_and_describes_the_server()
    {
        var known = (await CallAsync("initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "t", version = "1" } })).GetProperty("result");
        Assert.Equal("2025-03-26", known.GetProperty("protocolVersion").GetString());
        Assert.Equal("agentforge", known.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(known.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.Contains("approve_plan", known.GetProperty("instructions").GetString());

        var unknown = (await CallAsync("initialize", new { protocolVersion = "1999-01-01" })).GetProperty("result");
        Assert.Equal("2025-06-18", unknown.GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Notifications_get_202_without_a_body_and_get_is_not_allowed()
    {
        var response = await _mcp.PostAsync("/mcp", Rpc(new { jsonrpc = "2.0", method = "notifications/initialized" }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _mcp.GetAsync("/mcp")).StatusCode);
    }

    [Fact]
    public async Task Protocol_errors_use_json_rpc_error_codes()
    {
        var parse = await _mcp.PostAsync("/mcp", new StringContent("{isto não é json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, parse.StatusCode);
        Assert.Equal(-32700, (await parse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetInt32());

        Assert.Equal(-32601, (await CallAsync("resources/list")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32602, (await CallAsync("tools/call", new { name = "nao_existe" })).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32602, (await CallAsync("tools/call", new { })).GetProperty("error").GetProperty("code").GetInt32());

        var invalid = await _mcp.PostAsync("/mcp", Rpc(new { id = 1, method = "ping" }));   // sem "jsonrpc": "2.0"
        Assert.Equal(-32600, (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Batches_return_an_array_and_skip_notifications()
    {
        var response = await _mcp.PostAsync("/mcp", Rpc(new object[]
        {
            new { jsonrpc = "2.0", id = 1, method = "ping" },
            new { jsonrpc = "2.0", method = "notifications/initialized" },
            new { jsonrpc = "2.0", id = 2, method = "tools/list" },
        }));

        var replies = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, replies.GetArrayLength());
        Assert.Equal([1, 2], replies.EnumerateArray().Select(r => r.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task Oversized_bodies_are_rejected()
    {
        var huge = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"x\":\"" + new string('a', 600_000) + "\"}}",
            Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await _mcp.PostAsync("/mcp", huge)).StatusCode);
    }

    // ---------------------------------------------------------------------------- catálogo


    [Fact]
    public async Task Environment_load_tool_reports_every_environment_read_only()
    {
        var (isError, text) = await ToolAsync("get_environment_load");

        Assert.False(isError);
        var report = JsonDocument.Parse(text).RootElement;
        var first = report.GetProperty("environments")[0];
        Assert.Equal("default", first.GetProperty("kind").GetString());
        Assert.Equal(0, first.GetProperty("queuedTasks").GetInt32());
        Assert.Equal(0, report.GetProperty("waiting").GetArrayLength());
    }

    [Fact]
    public async Task Tool_catalog_is_complete_and_well_described()
    {
        var tools = (await CallAsync("tools/list")).GetProperty("result").GetProperty("tools");
        var byName = tools.EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);

        string[] expected =
        [
            "list_projects", "get_project", "create_project", "create_demand", "start_demand", "approve_plan", "get_demand",
            "list_tasks", "get_task", "get_task_diff", "get_run_output", "approve_task", "reject_task", "retry_task",
            "cancel_task", "list_pending_actions", "list_agents", "test_agent", "get_environment_load", "open_pull_request", "update_project",
        ];
        Assert.Equal(expected.Order(), byName.Keys.Order());
        foreach (var tool in byName.Values)
        {
            Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
            Assert.NotEmpty(tool.GetProperty("description").GetString()!);
        }
        Assert.True(byName["list_projects"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.True(byName["get_task"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.False(byName["approve_task"].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.True(byName["cancel_task"].GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.DoesNotContain("delete_project", byName.Keys);   // nada que apague projetos ou faça push/merge
        Assert.Equal(["taskId", "comment"], byName["reject_task"].GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(x => x.GetString()));
    }

    // ---------------------------------------------------------------------------- ferramentas

    [Fact]
    public async Task Project_and_demand_tools_roundtrip_with_input_validation()
    {
        var created = await ToolJsonAsync("create_project", new { name = "API de tarefas", repositoryUrl = "/repos/x.git", buildCommand = "dotnet build", autoMode = true });
        var projectId = created.GetProperty("id").GetGuid();
        Assert.Equal("main", created.GetProperty("baseBranch").GetString());
        Assert.True(created.GetProperty("autoMode").GetBoolean());

        Assert.Single((await ToolJsonAsync("list_projects")).EnumerateArray());
        Assert.Equal(projectId, (await ToolJsonAsync("get_project", new { projectId })).GetProperty("project").GetProperty("id").GetGuid());

        var demand = await ToolJsonAsync("create_demand", new { projectId, title = "Filtro por status", description = "GET /tasks?status=Done" });
        var demandId = demand.GetProperty("demand").GetProperty("id").GetGuid();
        Assert.Equal("New", demand.GetProperty("demand").GetProperty("status").GetString());

        var view = await ToolJsonAsync("get_demand", new { demandId });
        Assert.Equal("start_demand", view.GetProperty("nextAction").GetString());
        Assert.Equal(0, view.GetProperty("costUsd").GetDecimal());

        // erros de uso voltam como resultado com isError, para o modelo corrigir
        Assert.Contains("obrigatório", (await ToolAsync("create_project", new { name = "sem url" })).Text);
        Assert.Contains("UUID", (await ToolAsync("get_project", new { projectId = "isto-nao-e-uuid" })).Text);
        Assert.Contains("não encontrado", (await ToolAsync("get_project", new { projectId = Guid.NewGuid() })).Text);
        Assert.Contains("não encontrado", (await ToolAsync("create_demand", new { projectId = Guid.NewGuid(), title = "x" })).Text);
        Assert.Contains("inválida", (await ToolAsync("create_project", new { name = "x", repositoryUrl = "--upload-pack=x" })).Text);
        Assert.True((await ToolAsync("list_tasks", new { state = "Inexistente" })).IsError);
    }

    [Fact]
    public async Task Task_tools_show_state_findings_next_action_and_empty_diff()
    {
        var (projectId, demandId, itemId) = await SeedAsync(WorkItemState.AwaitingApproval);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ReviewFindings.Add(new ReviewFinding { WorkItemId = itemId, Role = AgentRole.TechLead, Message = "Renomear X", Resolved = true });
            db.AgentRuns.Add(new AgentRun { WorkItemId = itemId, DemandId = demandId, Role = AgentRole.Dev, Prompt = "p", Output = new string('x', 700), CostUsd = 0.25m, Status = AgentRunStatus.Succeeded });
            await db.SaveChangesAsync();
        }

        var task = await ToolJsonAsync("get_task", new { taskId = itemId });
        Assert.Equal("AwaitingApproval", task.GetProperty("task").GetProperty("state").GetString());
        Assert.Contains("approve_task", task.GetProperty("nextAction").GetString());
        Assert.Equal("Renomear X", task.GetProperty("findings")[0].GetProperty("message").GetString());
        Assert.Equal(0.25m, task.GetProperty("costUsd").GetDecimal());
        Assert.Contains("critério", task.GetProperty("acceptanceCriteria").GetString());

        Assert.Equal("(sem mudanças no branch ainda)", (await ToolAsync("get_task_diff", new { taskId = itemId })).Text);

        var runId = task.GetProperty("runs")[0].GetProperty("id").GetGuid();
        // maxChars abaixo do mínimo (500) é elevado ao mínimo; acima do tamanho da saída não trunca
        var output = await ToolJsonAsync("get_run_output", new { runId, maxChars = 5 });
        Assert.Equal(new string('x', 500) + "\n… (truncado)", output.GetProperty("output").GetString());
        var whole = await ToolJsonAsync("get_run_output", new { runId, maxChars = 5000 });
        Assert.Equal(700, whole.GetProperty("output").GetString()!.Length);

        var listed = await ToolJsonAsync("list_tasks", new { projectId, state = "awaitingapproval" });
        Assert.Single(listed.EnumerateArray());
        Assert.Empty((await ToolJsonAsync("list_tasks", new { demandId, state = "Done" })).EnumerateArray());
    }

    [Fact]
    public async Task Human_actions_respect_the_workflow_and_report_clear_errors()
    {
        var (_, _, backlog) = await SeedAsync(WorkItemState.Backlog);

        // rejeitar só vale na entrega; sem comentário nem chega a tentar
        Assert.Contains("não vale para o estado atual", (await ToolAsync("reject_task", new { taskId = backlog, comment = "refaça" })).Text);
        Assert.Contains("obrigatório", (await ToolAsync("reject_task", new { taskId = backlog })).Text);
        Assert.Contains("não vale para o estado atual", (await ToolAsync("retry_task", new { taskId = backlog })).Text);
        Assert.Contains("não encontrado", (await ToolAsync("approve_task", new { taskId = Guid.NewGuid() })).Text);
        Assert.Contains("não encontrado", (await ToolAsync("approve_plan", new { demandId = Guid.NewGuid() })).Text);

        // cancelar vale em qualquer estado ativo e é definitivo
        Assert.Equal("Cancelada.", (await ToolAsync("cancel_task", new { taskId = backlog })).Text);
        Assert.Equal("Cancelled", (await ToolJsonAsync("get_task", new { taskId = backlog })).GetProperty("task").GetProperty("state").GetString());
        Assert.Contains("não vale para o estado atual", (await ToolAsync("cancel_task", new { taskId = backlog })).Text);
    }

    [Fact]
    public async Task Pending_actions_gather_everything_that_waits_for_a_human()
    {
        var (_, demand1, _) = await SeedAsync(WorkItemState.Backlog);
        var (_, _, delivery) = await SeedAsync(WorkItemState.AwaitingApproval);
        var (_, _, blocked) = await SeedAsync(WorkItemState.Blocked);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.WorkItems.Single(w => w.Id == blocked).BlockedReason = "voltou 3 vezes";
            await db.SaveChangesAsync();
        }

        var pending = await ToolJsonAsync("list_pending_actions");

        Assert.Equal(demand1, pending.GetProperty("plansToApprove")[0].GetProperty("demandId").GetGuid());
        Assert.Equal(delivery, pending.GetProperty("deliveriesToReview")[0].GetProperty("taskId").GetGuid());
        Assert.Equal("voltou 3 vezes", pending.GetProperty("blocked")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Agent_tools_list_the_clis_and_reject_unknown_names()
    {
        var agents = await ToolJsonAsync("list_agents");
        var claude = agents.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "claude-code");
        Assert.Equal(5, claude.GetProperty("usedByRoles").GetArrayLength());

        var (isError, text) = await ToolAsync("test_agent", new { name = "nao-existe" });
        Assert.True(isError);
        Assert.Contains("não existe", text);
    }
}
