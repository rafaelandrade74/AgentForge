using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Domain;
using AgentForge.Orchestrator.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentForge.Tests.Integration;

/// <summary>CLI falso com conta: "login" só funciona com o código certo e grava um arquivo de estado.</summary>
public sealed class FakeAccountAdapter(string stateFile, string executable = "sh") : ICliAgentAdapter, ICliAgentAccount
{
    public const string RightCode = "codigo-certo";
    private readonly ClaudeCodeAdapter _claude = new();

    public string Name => "fake-cli";
    public string Executable => executable;
    public string DisplayName => "CLI falso";

    public IReadOnlyList<string> VersionArguments => ["-c", "echo 'fake-cli 1.2.3'"];
    public IReadOnlyList<string> StatusArguments =>
        ["-c", $$"""if [ -f '{{stateFile}}' ]; then echo '{"loggedIn":true,"authMethod":"fake","email":"voce@exemplo.com"}'; else echo '{"loggedIn":false,"authMethod":"none"}'; exit 1; fi"""];
    public IReadOnlyList<string>? LoginArguments =>
        ["-c", $$"""echo "Abra o link: https://exemplo.local/auth?x=1&y=2"; printf 'Cole o codigo > '; read code; if [ "$code" = '{{RightCode}}' ]; then touch '{{stateFile}}'; exit 0; else echo 'codigo invalido' >&2; exit 1; fi"""];
    public IReadOnlyList<string>? LogoutArguments => ["-c", $"rm -f '{stateFile}'"];
    public string TestPrompt => "diga OK";

    public AgentAuthState ParseStatus(string stdout, int exitCode) => _claude.ParseStatus(stdout, exitCode);
    public string? ExtractLoginUrl(string line) => _claude.ExtractLoginUrl(line);

    public IReadOnlyList<string> BuildArguments(AgentRequest request) => ["-c", "cat >/dev/null; echo 'result:OK, tudo certo'"];
    public AgentEventData? ParseEvent(string line) => null;
    public AgentResult ParseResult(IReadOnlyList<string> stdoutLines, int exitCode) =>
        new(stdoutLines.LastOrDefault(l => l.StartsWith("result:"))?[7..] ?? "", null, 0.01m, exitCode != 0);
}

[Collection(PostgresCollection.Name)]
public sealed class AgentCliServiceTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-agent-" + Guid.NewGuid().ToString("N"));
    private string State => Path.Combine(_temp, "logado");
    private ServiceProvider _provider = null!;
    private string _cs = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        _cs = pg.NewConnectionString();
        _provider = Build(new FakeAccountAdapter(State));
        await using var db = await Db.CreateAsync(_cs);
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        Directory.Delete(_temp, recursive: true);
    }

    private ServiceProvider Build(ICliAgentAdapter adapter)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AgentForge.Infrastructure.Persistence.AppDbContext>(o => o.UseNpgsql(_cs));
        services.AddSingleton(adapter);
        services.AddSingleton<AgentAdapterRegistry>();
        services.AddSingleton<IAgentEventPublisher, NullAgentEventPublisher>();
        services.AddSingleton<AgentForge.Execution.IProcessLauncher, AgentForge.Execution.LocalProcessLauncher>();
        services.AddScoped<IAgentRunner, CliAgentRunner>();
        services.AddSingleton<AgentCliService>();
        return services.BuildServiceProvider();
    }

    private AgentCliService Service => _provider.GetRequiredService<AgentCliService>();

    [PosixFact]
    public async Task Reports_installed_version_and_not_connected()
    {
        var info = (await Service.ListAsync(default)).Single();

        Assert.Equal("fake-cli", info.Name);
        Assert.True(info.Installed);
        Assert.Equal("fake-cli 1.2.3", info.Version);
        Assert.False(info.Authenticated);
        Assert.True(info.SupportsLogin);
    }

    [IntegrationFact]
    public async Task Missing_binary_is_reported_as_not_installed()
    {
        await using var provider = Build(new FakeAccountAdapter(State, executable: "binario-que-nao-existe-xyz"));

        var info = (await provider.GetRequiredService<AgentCliService>().ListAsync(default)).Single();

        Assert.False(info.Installed);
        Assert.False(info.Authenticated);
        Assert.Contains("não encontrado", info.Detail);
    }

    [PosixFact]
    public async Task Login_by_link_with_the_right_code_connects_the_agent()
    {
        var started = await Service.StartLoginAsync("fake-cli", default);

        Assert.Equal(LoginState.WaitingForCode, started.State);
        Assert.Equal("https://exemplo.local/auth?x=1&y=2", started.Url);

        var done = await Service.SubmitCodeAsync("fake-cli", started.SessionId, FakeAccountAdapter.RightCode, default);

        Assert.Equal(LoginState.Succeeded, done.State);
        Assert.Contains("voce@exemplo.com", done.Message);
        var info = (await Service.ListAsync(default)).Single();
        Assert.True(info.Authenticated);
        Assert.Equal("fake", info.AuthMethod);
        Assert.Contains("voce@exemplo.com", info.Detail);
    }

    [PosixFact]
    public async Task Wrong_code_fails_with_the_cli_message_and_a_new_login_can_be_started()
    {
        var first = await Service.StartLoginAsync("fake-cli", default);
        var failed = await Service.SubmitCodeAsync("fake-cli", first.SessionId, "errado", default);

        Assert.Equal(LoginState.Failed, failed.State);
        Assert.Contains("codigo invalido", failed.Message);
        Assert.False((await Service.GetAsync("fake-cli", force: true, default)).Authenticated);

        var second = await Service.StartLoginAsync("fake-cli", default);
        var ok = await Service.SubmitCodeAsync("fake-cli", second.SessionId, FakeAccountAdapter.RightCode, default);
        Assert.Equal(LoginState.Succeeded, ok.State);
    }

    [PosixFact]
    public async Task Invalid_codes_are_rejected_without_touching_the_cli()
    {
        var session = await Service.StartLoginAsync("fake-cli", default);

        foreach (var bad in new[] { "", "   ", "linha1\nlinha2", new string('x', 700) })
        {
            var result = await Service.SubmitCodeAsync("fake-cli", session.SessionId, bad, default);
            Assert.Equal(LoginState.WaitingForCode, result.State);
            Assert.Contains("inválido", result.Message);
        }

        // continua aceitando o código certo
        Assert.Equal(LoginState.Succeeded, (await Service.SubmitCodeAsync("fake-cli", session.SessionId, FakeAccountAdapter.RightCode, default)).State);
    }

    [PosixFact]
    public async Task Cancelling_or_restarting_stops_the_previous_login()
    {
        var first = await Service.StartLoginAsync("fake-cli", default);
        var cancelled = Service.CancelLogin("fake-cli", first.SessionId);
        Assert.Equal(LoginState.Cancelled, cancelled.State);

        var second = await Service.StartLoginAsync("fake-cli", default);
        var third = await Service.StartLoginAsync("fake-cli", default);
        Assert.Equal(LoginState.Cancelled, Service.GetLogin("fake-cli", second.SessionId).State);
        Assert.Equal(LoginState.WaitingForCode, Service.GetLogin("fake-cli", third.SessionId).State);
        Service.CancelLogin("fake-cli", third.SessionId);
    }

    [PosixFact]
    public async Task Idle_login_times_out()
    {
        var previous = AgentCliService.LoginIdleTimeout;
        AgentCliService.LoginIdleTimeout = TimeSpan.FromSeconds(1);
        try
        {
            var session = await Service.StartLoginAsync("fake-cli", default);
            Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(
                Service.GetLogin("fake-cli", session.SessionId).State == LoginState.Failed), 10));
            Assert.Contains("Tempo esgotado", Service.GetLogin("fake-cli", session.SessionId).Message);
        }
        finally
        {
            AgentCliService.LoginIdleTimeout = previous;
        }
    }

    [PosixFact]
    public async Task Test_fails_without_connection_and_passes_when_connected_without_saving_a_run()
    {
        var notConnected = await Service.TestAsync("fake-cli", default);
        Assert.False(notConnected.Ok);
        Assert.Contains("Não conectado", notConnected.Message);

        await File.WriteAllTextAsync(State, "");
        var connected = await Service.TestAsync("fake-cli", default);

        Assert.True(connected.Ok);
        Assert.Equal("OK, tudo certo", connected.Message);
        Assert.Equal(connected, (await Service.ListAsync(default)).Single().LastTest);
        await using var db = await Db.CreateAsync(_cs);
        Assert.Empty(await db.AgentRuns.ToListAsync());   // teste avulso não gera AgentRun
    }

    [PosixFact]
    public async Task Logout_disconnects()
    {
        await File.WriteAllTextAsync(State, "");
        Assert.True((await Service.GetAsync("fake-cli", force: true, default)).Authenticated);

        var info = await Service.LogoutAsync("fake-cli", default);

        Assert.False(info.Authenticated);
    }

    [IntegrationFact]
    public async Task Unknown_agent_is_rejected()
    {
        await Assert.ThrowsAsync<AgentNotFoundException>(() => Service.GetAsync("nao-existe", false, default));
        await Assert.ThrowsAsync<AgentNotFoundException>(() => Service.StartLoginAsync("nao-existe", default));
        await Assert.ThrowsAsync<AgentNotFoundException>(() => Task.FromResult(Service.GetLogin("fake-cli", "sessao-inexistente")));
    }
}

public class ClaudeCodeAccountParsingTests
{
    private readonly ClaudeCodeAdapter _adapter = new();

    [Fact]
    public void Parses_real_status_output_when_not_logged_in()
    {
        var state = _adapter.ParseStatus("""
            {
              "loggedIn": false,
              "authMethod": "none",
              "apiProvider": "firstParty",
              "configDirectory": "/home/app/.claude"
            }
            """, 1);

        Assert.False(state.Authenticated);
        Assert.Equal("none", state.Method);
    }

    [Fact]
    public void Parses_logged_in_status_with_account_details()
    {
        var state = _adapter.ParseStatus("""{"loggedIn":true,"authMethod":"claude.ai","email":"a@b.com","subscriptionType":"max"}""", 0);

        Assert.True(state.Authenticated);
        Assert.Equal("claude.ai", state.Method);
        Assert.Equal("a@b.com · max", state.Detail);
    }

    [Fact]
    public void Garbage_status_is_not_authenticated() =>
        Assert.False(_adapter.ParseStatus("erro qualquer", 1).Authenticated);

    [Theory]
    [InlineData("If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true&state=abc", "https://claude.com/cai/oauth/authorize?code=true&state=abc")]
    [InlineData("Opening browser to sign in…", null)]
    [InlineData("Paste code here if prompted > ", null)]
    public void Extracts_the_login_link(string line, string? expected) =>
        Assert.Equal(expected, _adapter.ExtractLoginUrl(line));

    [Fact]
    public void Account_commands_use_claude_auth()
    {
        Assert.Equal(["auth", "login"], _adapter.LoginArguments);
        Assert.Equal(["auth", "logout"], _adapter.LogoutArguments);
        Assert.Equal("auth", _adapter.StatusArguments[0]);
    }
}

[Collection(PostgresCollection.Name)]
public sealed class AgentEndpointsTests(PostgresFixture pg) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(pg.NewConnectionString());
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

    [IntegrationFact]
    public async Task Agent_endpoints_require_authentication()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/agents/claude-code/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/agents/claude-code/test", null)).StatusCode);
    }

    [IntegrationFact]
    public async Task Lists_claude_code_with_the_roles_that_use_it()
    {
        var agents = await _http.GetFromJsonAsync<JsonElement>("/api/agents");

        var claude = agents.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "claude-code");
        Assert.Equal("Claude Code", claude.GetProperty("displayName").GetString());
        Assert.True(claude.GetProperty("supportsLogin").GetBoolean());
        Assert.Equal(5, claude.GetProperty("usedByRoles").GetArrayLength());
    }

    [IntegrationFact]
    public async Task Environment_tells_where_the_clis_run()
    {
        var env = await _http.GetFromJsonAsync<JsonElement>("/api/agents/environment");

        Assert.Equal("local", env.GetProperty("mode").GetString());
        Assert.NotEmpty(env.GetProperty("description").GetString()!);
    }

    [IntegrationFact]
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
            Assert.Contains("SPA-AgentForge", await client.GetStringAsync("/projects/abc/demands/new"));   // rota do Angular
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

    [IntegrationFact]
    public async Task Unknown_agent_returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/agents/nao-existe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/agents/nao-existe/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/agents/nao-existe/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/agents/claude-code/login/sessao-inexistente")).StatusCode);
    }

    [IntegrationFact]
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
