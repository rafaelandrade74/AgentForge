using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.Tests;

/// <summary>
/// Ambientes de execução (fase 10): uma "máquina remota" é um host agent de verdade, em processo, com a própria pasta
/// de clones; o ambiente padrão é o processo da API. Só roda em Linux/macOS (o host agent do teste libera `sh`).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EnvironmentTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Token = "token-do-host-agent-com-mais-de-24-caracteres";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-env-" + Guid.NewGuid().ToString("N"));
    private string RemoteRoot => Path.Combine(_temp, "remote");
    private string DefaultRoot => Path.Combine(_temp, "default");
    private WebApplication? _agent;
    private string _agentUrl = "";
    private Factory _factory = null!;
    private HttpClient _http = null!;
    private readonly string _cs = pg.NewConnectionString();

    private sealed class Factory(string cs, string temp) : ApiFactory(cs)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Workspaces:Root", Path.Combine(temp, "default"));
            builder.UseSetting("DataProtection:KeysDir", Path.Combine(temp, "keys"));
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(RemoteRoot);
        Directory.CreateDirectory(DefaultRoot);
        if (OperatingSystem.IsWindows()) return;

        _agent = AgentForge.Tests.Orchestrator.HostAgentFactory.Create(RemoteRoot, Token);
        await _agent.StartAsync();
        _agentUrl = _agent.Urls.First();

        _factory = new Factory(_cs, _temp);
        _http = _factory.CreateClient();
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_agent is not null) await _agent.DisposeAsync();
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
    }

    private object Body(string name = "maquina-a", string? url = null, string? token = Token, string? root = null, string tags = "node20, dotnet8") =>
        new { name, url = url ?? _agentUrl, token, workspaceRoot = root ?? $"{RemoteRoot}/a", tags };

    private async Task<JsonElement> CreateEnvironment(object? body = null)
    {
        var response = await _http.PostAsJsonAsync("/api/environments", body ?? Body());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<Project> CreateProject(string repositoryUrl, Guid? environmentId)
    {
        var response = await _http.PostAsJsonAsync("/api/projects", new { name = "P" + Guid.NewGuid().ToString("N")[..6], repositoryUrl, environmentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var returned = json.GetProperty("environmentId");
        Assert.Equal(environmentId, returned.ValueKind == JsonValueKind.Null ? (Guid?)null : returned.GetGuid());
        return new Project { Id = json.GetProperty("id").GetGuid(), Name = "P", RepositoryUrl = repositoryUrl, BaseBranch = "main", EnvironmentId = environmentId };
    }

    [PosixFact]
    public async Task Requires_the_login_and_rejects_mcp_access_tokens()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/environments")).StatusCode);

        var created = await _http.PostAsJsonAsync("/api/tokens", new { name = "mcp" });
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var mcp = _factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mcp.GetAsync("/api/environments")).StatusCode);
    }

    [PosixFact]
    public async Task The_token_is_stored_protected_and_never_returned()
    {
        var created = await CreateEnvironment();
        var id = created.GetProperty("id").GetGuid();

        Assert.True(created.GetProperty("hasToken").GetBoolean());
        var list = await (await _http.GetAsync("/api/environments")).Content.ReadAsStringAsync();
        var edited = await (await _http.PutAsJsonAsync($"/api/environments/{id}", Body(token: null, tags: "python3"))).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Token, created.GetRawText() + list + edited);

        await using var db = await Db.CreateAsync(_cs);
        var stored = await db.ExecutionEnvironments.AsNoTracking().SingleAsync(e => e.Id == id);
        Assert.NotEmpty(stored.ProtectedToken);
        Assert.DoesNotContain(Token, stored.ProtectedToken);
        Assert.Equal(Token, _factory.Services.GetRequiredService<ISecretProtector>().Unprotect(stored.ProtectedToken));
        Assert.Equal("python3", stored.Tags);   // editar sem token mantém o token e muda o resto
    }

    [PosixFact]
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

    [PosixFact]
    public async Task A_project_needs_an_existing_environment_and_an_environment_in_use_cannot_be_deleted()
    {
        var bad = await _http.PostAsJsonAsync("/api/projects", new { name = "x", repositoryUrl = "https://h/r.git", environmentId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var id = (await CreateEnvironment()).GetProperty("id").GetGuid();
        await CreateProject("https://h/r.git", id);

        var refused = await _http.DeleteAsync($"/api/environments/{id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("1 projeto", await refused.Content.ReadAsStringAsync());

        var free = (await CreateEnvironment(Body("livre", root: $"{RemoteRoot}/livre"))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/environments/{free}")).StatusCode);
    }

    [PosixFact]
    public async Task Lists_the_default_environment_and_how_many_projects_use_each()
    {
        var id = (await CreateEnvironment()).GetProperty("id").GetGuid();
        await CreateProject("https://h/a.git", id);
        await CreateProject("https://h/b.git", id);

        var list = await (await _http.GetAsync("/api/environments")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("local", list.GetProperty("default").GetProperty("mode").GetString());
        Assert.Equal(2, list.GetProperty("items")[0].GetProperty("projectCount").GetInt32());
    }

    [PosixFact]
    public async Task The_connection_test_reports_the_tools_of_the_machine()
    {
        var id = (await CreateEnvironment(Body(root: RemoteRoot))).GetProperty("id").GetGuid();

        var result = await (await _http.PostAsync($"/api/environments/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(result.GetProperty("reachable").GetBoolean());
        Assert.True(result.GetProperty("workspaceRootExists").GetBoolean());
        var tools = result.GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("tool").GetString()!, t => t);
        Assert.True(tools["git"].GetProperty("ok").GetBoolean());
        Assert.Contains("git version", tools["git"].GetProperty("detail").GetString());
        // o host agent do teste só libera claude, git e sh: o resto aparece como indisponível, sem derrubar o teste
        Assert.False(tools["dotnet"].GetProperty("ok").GetBoolean());
    }

    [PosixFact]
    public async Task The_connection_test_of_an_offline_machine_says_so()
    {
        var id = (await CreateEnvironment(Body(url: "http://127.0.0.1:1"))).GetProperty("id").GetGuid();

        var result = await (await _http.PostAsync($"/api/environments/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(result.GetProperty("reachable").GetBoolean());
        Assert.Contains("inacessível", result.GetProperty("error").GetString());
        Assert.DoesNotContain(Token, result.GetRawText());
    }

    [PosixFact]
    public async Task A_wrong_token_is_reported_without_leaking_it()
    {
        var id = (await CreateEnvironment(Body(token: "outro-token-com-mais-de-24-caracteres-x"))).GetProperty("id").GetGuid();

        var result = await (await _http.PostAsync($"/api/environments/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(result.GetProperty("reachable").GetBoolean());
        Assert.DoesNotContain("outro-token", result.GetRawText());
    }

    [PosixFact]
    public async Task Projects_clone_into_the_workspace_root_of_their_own_environment()
    {
        var origin = await GitOrigin.CreateAsync(_temp);
        var a = (await CreateEnvironment(Body("maquina-a", root: $"{RemoteRoot}/a"))).GetProperty("id").GetGuid();
        var b = (await CreateEnvironment(Body("maquina-b", root: $"{RemoteRoot}/b"))).GetProperty("id").GetGuid();
        var inA = await CreateProject(origin, a);
        var inB = await CreateProject(origin, b);
        var inDefault = await CreateProject(origin, null);
        var workspaces = _factory.Services.GetRequiredService<IWorkspaceManager>();

        await workspaces.EnsureRepositoryAsync(inA, default);
        await workspaces.EnsureRepositoryAsync(inB, default);
        await workspaces.EnsureRepositoryAsync(inDefault, default);

        Assert.True(Directory.Exists($"{RemoteRoot}/a/{inA.Id}/repo/.git"));
        Assert.True(Directory.Exists($"{RemoteRoot}/b/{inB.Id}/repo/.git"));
        Assert.True(Directory.Exists($"{DefaultRoot}/{inDefault.Id}/repo/.git"));
        Assert.False(Directory.Exists($"{DefaultRoot}/{inA.Id}"));
        Assert.False(Directory.Exists($"{RemoteRoot}/a/{inB.Id}"));

        // worktree e diff também acontecem na máquina do projeto
        var task = Guid.NewGuid();
        var ws = await workspaces.CreateWorktreeAsync(inA, task, default);
        Assert.Equal($"{RemoteRoot}/a/{inA.Id}/worktrees/{task}", ws.Path);
        Assert.True(Directory.Exists(ws.Path));
    }

    [PosixFact]
    public async Task The_agent_runs_on_the_machine_of_the_project_of_the_request()
    {
        var env = (await CreateEnvironment(Body(root: $"{RemoteRoot}/a"))).GetProperty("id").GetGuid();
        var remote = await CreateProject("https://h/r.git", env);
        var local = await CreateProject("https://h/l.git", null);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runner = new CliAgentRunner(new AgentAdapterRegistry([new FakeCliAgentAdapter("""echo 'result:{"ok":true}'""")]),
            db, new CollectingPublisher(), scope.ServiceProvider.GetRequiredService<IEnvironmentLaunchers>(), NullLogger<CliAgentRunner>.Instance);

        // a pasta do ambiente padrão está fora das pastas permitidas do host agent: só o projeto padrão consegue rodar nela
        var remoteInItsRoot = await runner.RunAsync(new AgentRequest(AgentRole.Dev, "x", RemoteRoot, "fake", ProjectId: remote.Id), default);
        var remoteInDefaultRoot = await runner.RunAsync(new AgentRequest(AgentRole.Dev, "x", DefaultRoot, "fake", ProjectId: remote.Id), default);
        var localInDefaultRoot = await runner.RunAsync(new AgentRequest(AgentRole.Dev, "x", DefaultRoot, "fake", ProjectId: local.Id), default);

        Assert.Equal(AgentRunStatus.Succeeded, remoteInItsRoot.Status);
        Assert.Equal(AgentRunStatus.Failed, remoteInDefaultRoot.Status);
        Assert.Equal(AgentRunStatus.Succeeded, localInDefaultRoot.Status);
    }


    [PosixFact]
    public async Task An_offline_machine_fails_the_run_and_the_clone_with_a_clear_message()
    {
        var env = (await CreateEnvironment(Body(url: "http://127.0.0.1:1"))).GetProperty("id").GetGuid();
        var project = await CreateProject("https://h/r.git", env);

        var ex = await Assert.ThrowsAsync<ProcessStartException>(() =>
            _factory.Services.GetRequiredService<IWorkspaceManager>().EnsureRepositoryAsync(project, default));
        Assert.Contains("inacessível", ex.Message);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runner = new CliAgentRunner(new AgentAdapterRegistry([new FakeCliAgentAdapter("echo oi")]), db, new CollectingPublisher(),
            scope.ServiceProvider.GetRequiredService<IEnvironmentLaunchers>(), NullLogger<CliAgentRunner>.Instance);
        var run = await runner.RunAsync(new AgentRequest(AgentRole.Dev, "x", RemoteRoot, "fake", ProjectId: project.Id), default);

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains("inacessível", run.Output);
    }

    [PosixFact]
    public async Task A_token_that_can_no_longer_be_decrypted_asks_for_it_again()
    {
        var id = (await CreateEnvironment()).GetProperty("id").GetGuid();
        await using (var db = await Db.CreateAsync(_cs))
            await db.ExecutionEnvironments.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.ProtectedToken, "lixo-nao-decifravel"));
        var project = await CreateProject("https://h/r.git", id);

        var ex = await Assert.ThrowsAsync<EnvironmentUnavailableException>(() =>
            _factory.Services.GetRequiredService<IWorkspaceManager>().EnsureRepositoryAsync(project, default));

        Assert.Contains("informe o token de novo", ex.Message);
        var result = await (await _http.PostAsync($"/api/environments/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("reachable").GetBoolean());
    }

    [PosixFact]
    public async Task Changing_the_token_of_an_environment_takes_effect_without_restarting()
    {
        var id = (await CreateEnvironment(Body(token: "token-antigo-com-mais-de-24-caracteres-x"))).GetProperty("id").GetGuid();
        var before = await (await _http.PostAsync($"/api/environments/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(before.GetProperty("reachable").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await _http.PutAsJsonAsync($"/api/environments/{id}", Body())).StatusCode);

        var after = await (await _http.PostAsync($"/api/environments/{id}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(after.GetProperty("reachable").GetBoolean());
    }

    [PosixFact]
    public async Task The_default_environment_test_works_without_any_registered_machine()
    {
        var result = await (await _http.PostAsync("/api/environments/default/test", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(result.GetProperty("reachable").GetBoolean());
        Assert.True(result.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("tool").GetString() == "git").GetProperty("ok").GetBoolean());
    }
}
