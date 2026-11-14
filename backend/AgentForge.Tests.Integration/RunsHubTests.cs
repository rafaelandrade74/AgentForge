using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Agents;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Tests.Integration;

/// <summary>API completa (HTTP + SignalR) com o orquestrador real e um CLI falso. Só roda em Linux/macOS.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RunsHubTests(PostgresFixture pg) : IAsyncLifetime
{
    private record Received(long AtMs, string Method, JsonElement Payload);

    private sealed class HubFactory(string cs, string reposRoot, ScriptedAdapter adapter) : ApiFactory(cs)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Workspaces:Root", reposRoot);
            builder.ConfigureServices(s => s.AddSingleton<ICliAgentAdapter>(adapter));
        }
    }

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-hub-" + Guid.NewGuid().ToString("N"));
    private string _origin = "";
    private readonly ScriptedAdapter _adapter = new();
    private HubFactory _factory = null!;
    private HttpClient _http = null!;
    private string _token = "";
    private readonly List<HubConnection> _connections = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        if (OperatingSystem.IsWindows()) return;

        _origin = await GitOrigin.CreateAsync(_temp);
        _factory = new HubFactory(pg.NewConnectionString(), Path.Combine(_temp, "repos"), _adapter);
        _http = _factory.CreateClient();   // sobe a API: migrations, seed e orquestrador

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().AgentRoleConfigs
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.AdapterName, "fake"));

        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        _token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        _http?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
    }

    private HubConnection Connect(string? token, ConcurrentQueue<Received>? sink = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/runs"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                if (token is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        if (sink is not null)
            foreach (var method in new[] { "runEvent", "workItemChanged", "demandChanged" })
                connection.On<JsonElement>(method, p => sink.Enqueue(new Received(Now(), method, p)));
        _connections.Add(connection);
        return connection;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private async Task<(Project Project, Demand Demand)> NewDemandAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = new Project { Name = "P", RepositoryUrl = _origin, BaseBranch = "main", AutoMode = false };
        var demand = new Demand { ProjectId = project.Id, Title = "D", Description = "d" };
        db.AddRange(project, demand);
        await db.SaveChangesAsync();
        return (project, demand);
    }

    [PosixFact]
    public async Task Agent_log_reaches_the_browser_in_under_two_seconds()
    {
        // o agente imprime a hora de emissão em cada linha, com 1 s entre elas
        _adapter.RawShell[AgentRole.ScrumMaster] =
            """for i in 1 2 3; do echo "evt:$(date +%s%3N)"; sleep 1; done; printf 'result:{"tasks":[{"key":"a","title":"A","dependsOn":[]}]}\n'""";
        var (project, demand) = await NewDemandAsync();
        var received = new ConcurrentQueue<Received>();
        var hub = Connect(_token, received);
        await hub.StartAsync();
        await hub.InvokeAsync("JoinProject", project.Id);

        var start = await _http.PostAsync($"/api/demands/{demand.Id}/start", null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, start.StatusCode);

        Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(
            received.Any(r => r.Method == "runEvent" && r.Payload.GetProperty("kind").GetString() == "run_finished")), 30));

        var runEvents = received.Where(r => r.Method == "runEvent").ToList();
        var logs = runEvents.Where(r => r.Payload.GetProperty("kind").GetString() == "log").ToList();
        Assert.Equal(3, logs.Count);
        foreach (var log in logs)
        {
            var emittedAt = long.Parse(log.Payload.GetProperty("text").GetString()!);
            Assert.InRange(log.AtMs - emittedAt, 0, 1999);
        }

        // ciclo da execução e identificação
        Assert.Equal("run_started", runEvents[0].Payload.GetProperty("kind").GetString());
        Assert.Equal("Succeeded", runEvents[^1].Payload.GetProperty("text").GetString());
        Assert.All(runEvents, r =>
        {
            Assert.Equal(project.Id, r.Payload.GetProperty("projectId").GetGuid());
            Assert.Equal(demand.Id, r.Payload.GetProperty("demandId").GetGuid());
            Assert.Equal("ScrumMaster", r.Payload.GetProperty("role").GetString());
        });
        Assert.Single(runEvents.Select(r => r.Payload.GetProperty("runId").GetGuid()).Distinct());
    }

    [PosixFact]
    public async Task State_changes_of_demand_and_tasks_are_broadcast()
    {
        var (project, demand) = await NewDemandAsync();
        var received = new ConcurrentQueue<Received>();
        var hub = Connect(_token, received);
        await hub.StartAsync();
        await hub.InvokeAsync("JoinProject", project.Id);

        await _http.PostAsync($"/api/demands/{demand.Id}/start", null);
        Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(
            received.Any(r => r.Method == "workItemChanged")), 30));

        var statuses = received.Where(r => r.Method == "demandChanged").Select(r => r.Payload.GetProperty("status").GetString()).ToList();
        Assert.Equal(["Refining", "InProgress"], statuses);
        var item = received.First(r => r.Method == "workItemChanged").Payload;
        Assert.Equal("Backlog", item.GetProperty("state").GetString());
        Assert.Equal(project.Id, item.GetProperty("projectId").GetGuid());

        // aprovar o plano gera a transição seguinte, também transmitida
        await _http.PostAsync($"/api/demands/{demand.Id}/approve-plan", null);
        Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(
            received.Any(r => r.Method == "workItemChanged" && r.Payload.GetProperty("state").GetString() == "InDevelopment")), 30));
    }

    [PosixFact]
    public async Task Unexpected_refinement_error_still_broadcasts_the_failed_demand()
    {
        // repositório inexistente: o clone falha antes de qualquer agente rodar
        Guid projectId, demandId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var project = new Project { Name = "Quebrado", RepositoryUrl = Path.Combine(_temp, "nao-existe.git") };
            var demand = new Demand { ProjectId = project.Id, Title = "D" };
            db.AddRange(project, demand);
            await db.SaveChangesAsync();
            (projectId, demandId) = (project.Id, demand.Id);
        }
        var received = new ConcurrentQueue<Received>();
        var hub = Connect(_token, received);
        await hub.StartAsync();
        await hub.InvokeAsync("JoinProject", projectId);

        await _http.PostAsync($"/api/demands/{demandId}/start", null);

        Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(
            received.Any(r => r.Method == "demandChanged" && r.Payload.GetProperty("status").GetString() == "Failed")), 30));
    }

    [PosixFact]
    public async Task Clients_only_receive_events_of_the_projects_they_joined()
    {
        var (project, demand) = await NewDemandAsync();
        var (otherProject, _) = await NewDemandAsync();
        var joined = new ConcurrentQueue<Received>();
        var other = new ConcurrentQueue<Received>();
        var notJoined = new ConcurrentQueue<Received>();

        var a = Connect(_token, joined);
        var b = Connect(_token, other);
        var c = Connect(_token, notJoined);
        foreach (var conn in new[] { a, b, c }) await conn.StartAsync();
        await a.InvokeAsync("JoinProject", project.Id);
        await b.InvokeAsync("JoinProject", otherProject.Id);

        await _http.PostAsync($"/api/demands/{demand.Id}/start", null);
        Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(joined.Any(r => r.Method == "workItemChanged")), 30));
        await Task.Delay(500);

        Assert.NotEmpty(joined);
        Assert.Empty(other);
        Assert.Empty(notJoined);

        // depois de sair do grupo, não recebe mais
        await a.InvokeAsync("LeaveProject", project.Id);
        joined.Clear();
        await _http.PostAsync($"/api/demands/{demand.Id}/approve-plan", null);
        await Task.Delay(1500);
        Assert.Empty(joined);
    }

    [PosixFact]
    public async Task Hub_requires_a_valid_token()
    {
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Connect(null).StartAsync());
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Connect("token.invalido.aqui").StartAsync());

        var ok = Connect(_token);
        await ok.StartAsync();
        Assert.Equal(HubConnectionState.Connected, ok.State);
    }
}
