using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Execution;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Environments;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.Tests;

/// <summary>
/// Fase 11: credenciais Git e login do agente POR AMBIENTE. Cada "máquina" é um host agent de verdade, em processo, com a
/// própria pasta de chave SSH; o ambiente padrão é o servidor, com a pasta Git:SshDir. Os testes de chave precisam do
/// ssh-keygen (imagem backend/Dockerfile.tests) e, como todo teste de host agent, de Linux/macOS.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EnvironmentCredentialTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string TokenA = "token-da-maquina-a-com-mais-de-24-caracteres";
    private const string TokenB = "token-da-maquina-b-com-mais-de-24-caracteres";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-cred-" + Guid.NewGuid().ToString("N"));
    private string ServerSsh => Path.Combine(_temp, "server-ssh");
    private string SshA => Path.Combine(_temp, "ssh-a");
    private string SshB => Path.Combine(_temp, "ssh-b");
    private WebApplication? _agentA;
    private WebApplication? _agentB;
    private Factory _factory = null!;
    private HttpClient _http = null!;

    private sealed class Factory(string cs, string temp) : ApiFactory(cs)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Workspaces:Root", Path.Combine(temp, "default"));
            builder.UseSetting("DataProtection:KeysDir", Path.Combine(temp, "keys"));
            builder.UseSetting("Git:SshDir", Path.Combine(temp, "server-ssh"));
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        if (OperatingSystem.IsWindows()) return;

        _agentA = AgentForge.Tests.Orchestrator.HostAgentFactory.Create(Path.Combine(_temp, "root-a"), TokenA, SshA);
        _agentB = AgentForge.Tests.Orchestrator.HostAgentFactory.Create(Path.Combine(_temp, "root-b"), TokenB, SshB);
        await _agentA.StartAsync();
        await _agentB.StartAsync();

        _factory = new Factory(pg.NewConnectionString(), _temp);
        _http = _factory.CreateClient();
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_agentA is not null) await _agentA.DisposeAsync();
        if (_agentB is not null) await _agentB.DisposeAsync();
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
    }

    private async Task<Guid> CreateEnvironment(string name, string url, string token)
    {
        var response = await _http.PostAsJsonAsync("/api/environments",
            new { name, url, token, workspaceRoot = Path.Combine(_temp, "root-" + name[^1..].ToLowerInvariant(), "w"), tags = "" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<Guid> MachineA() => CreateEnvironment("maquina-A", _agentA!.Urls.First(), TokenA);
    private Task<Guid> MachineB() => CreateEnvironment("maquina-B", _agentB!.Urls.First(), TokenB);

    private Task<JsonElement> Json(HttpResponseMessage response) => response.Content.ReadFromJsonAsync<JsonElement>();

    // ------------------------------------------------------------------ chave SSH por ambiente

    [SshFact]
    public async Task Each_machine_generates_and_keeps_its_own_key_and_the_private_part_never_leaves()
    {
        var a = await MachineA();
        var b = await MachineB();

        var before = await Json(await _http.GetAsync($"/api/git/ssh?environmentId={a}"));
        Assert.True(before.GetProperty("supported").GetBoolean());
        Assert.False(before.GetProperty("hasKey").GetBoolean());

        var generatedA = await Json(await _http.PostAsync($"/api/git/ssh/generate?environmentId={a}", null));
        var generatedB = await Json(await _http.PostAsync($"/api/git/ssh/generate?environmentId={b}", null));

        Assert.StartsWith("ssh-ed25519 ", generatedA.GetProperty("publicKey").GetString());
        Assert.NotEqual(generatedA.GetProperty("publicKey").GetString(), generatedB.GetProperty("publicKey").GetString());
        Assert.StartsWith("SHA256:", generatedA.GetProperty("fingerprint").GetString());

        // a chave de cada ambiente mora na pasta DA PRÓPRIA máquina; o servidor não guarda a privada de ninguém
        Assert.True(File.Exists(Path.Combine(SshA, "id_ed25519")));
        Assert.True(File.Exists(Path.Combine(SshB, "id_ed25519")));
        Assert.False(File.Exists(Path.Combine(ServerSsh, "id_ed25519")));
        var privateA = await File.ReadAllTextAsync(Path.Combine(SshA, "id_ed25519"));
        Assert.DoesNotContain("PRIVATE KEY", generatedA.GetRawText() + generatedB.GetRawText());
        Assert.DoesNotContain(privateA.Trim(), generatedA.GetRawText());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(SshA, "id_ed25519")));

        // o ambiente padrão (servidor) continua sem chave: gerar numa máquina não mexe nas outras
        var server = await Json(await _http.GetAsync("/api/git/ssh"));
        Assert.False(server.GetProperty("hasKey").GetBoolean());
    }

    [SshFact]
    public async Task Regenerating_needs_confirmation_and_deleting_only_affects_that_machine()
    {
        var a = await MachineA();
        var b = await MachineB();
        var first = await Json(await _http.PostAsync($"/api/git/ssh/generate?environmentId={a}", null));
        var keyB = await Json(await _http.PostAsync($"/api/git/ssh/generate?environmentId={b}", null));

        var refused = await _http.PostAsync($"/api/git/ssh/generate?environmentId={a}", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Já existe", await refused.Content.ReadAsStringAsync());

        var replaced = await Json(await _http.PostAsJsonAsync($"/api/git/ssh/generate?environmentId={a}", new { overwrite = true }));
        Assert.NotEqual(first.GetProperty("publicKey").GetString(), replaced.GetProperty("publicKey").GetString());

        var deleted = await Json(await _http.DeleteAsync($"/api/git/ssh?environmentId={a}"));
        Assert.False(deleted.GetProperty("hasKey").GetBoolean());
        Assert.False(File.Exists(Path.Combine(SshA, "id_ed25519")));
        var stillB = await Json(await _http.GetAsync($"/api/git/ssh?environmentId={b}"));
        Assert.Equal(keyB.GetProperty("publicKey").GetString(), stillB.GetProperty("publicKey").GetString());
    }

    [SshFact]
    public async Task Git_on_a_machine_uses_that_machines_key_only_once_it_exists()
    {
        var a = await MachineA();
        var b = await MachineB();
        var launchers = _factory.Services.GetRequiredService<IEnvironmentLaunchers>();
        var machineA = (await launchers.ResolveAsync(a, default)).Launcher;
        var machineB = (await launchers.ResolveAsync(b, default)).Launcher;
        var scratch = await machineA.ScratchDirectoryAsync(default);

        // o git imprime o GIT_SSH_COMMAND que recebeu (alias de shell)
        async Task<string> SshCommandSeenByGit(IProcessLauncher launcher) =>
            (await launcher.RunAsync(new LaunchSpec("git", ["-c", "alias.k=!printenv GIT_SSH_COMMAND || true", "k"], scratch),
                TimeSpan.FromSeconds(20), default)).Stdout.Trim();

        Assert.Equal("", await SshCommandSeenByGit(machineA));

        await _http.PostAsync($"/api/git/ssh/generate?environmentId={a}", null);

        Assert.Contains(Path.Combine(SshA, "id_ed25519"), await SshCommandSeenByGit(machineA));
        Assert.Equal("", await SshCommandSeenByGit(machineB));   // a chave de A não vale em B
    }

    [PosixFact]
    public async Task An_unreachable_machine_is_reported_as_unsupported_and_changes_are_refused()
    {
        var offline = await CreateEnvironment("maquina-C", "http://127.0.0.1:1", TokenA);

        var status = await Json(await _http.GetAsync($"/api/git/ssh?environmentId={offline}"));
        Assert.False(status.GetProperty("supported").GetBoolean());
        Assert.Contains("inacessível", status.GetProperty("reason").GetString());
        Assert.DoesNotContain(TokenA, status.GetRawText());

        var generate = await _http.PostAsync($"/api/git/ssh/generate?environmentId={offline}", null);
        Assert.Equal(HttpStatusCode.Conflict, generate.StatusCode);
        Assert.Contains("inacessível", await generate.Content.ReadAsStringAsync());

        var test = await Json(await _http.PostAsJsonAsync($"/api/git/ssh/test?environmentId={offline}", new { repositoryUrl = "git@github.com:a/b.git" }));
        Assert.False(test.GetProperty("ok").GetBoolean());
    }

    [PosixFact]
    public async Task An_unknown_environment_is_a_conflict_not_a_silent_fallback_to_the_server()
    {
        var ghost = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Conflict, (await _http.GetAsync($"/api/git/ssh?environmentId={ghost}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsync($"/api/git/ssh/generate?environmentId={ghost}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.GetAsync($"/api/agents?environmentId={ghost}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsync($"/api/agents/claude-code/login?environmentId={ghost}", null)).StatusCode);
        Assert.False(File.Exists(Path.Combine(ServerSsh, "id_ed25519")));
    }

    [PosixFact]
    public async Task An_old_host_agent_without_ssh_support_says_to_update_it()
    {
        // um host agent "antigo": responde a tudo menos às rotas de chave
        var builder = WebApplication.CreateBuilder(["--urls=http://127.0.0.1:0"]);
        await using var old = builder.Build();
        old.MapGet("/v1/info", () => new HostInfo("old", "1", Path.GetTempPath(), [], []));
        await old.StartAsync();
        var env = await CreateEnvironment("maquina-D", old.Urls.First(), TokenA);

        var status = await Json(await _http.GetAsync($"/api/git/ssh?environmentId={env}"));

        Assert.False(status.GetProperty("supported").GetBoolean());
        Assert.Contains("atualize", status.GetProperty("reason").GetString());
    }

    // ------------------------------------------------------------------ agentes por ambiente

    [PosixFact]
    public async Task The_agents_screen_targets_the_chosen_machine()
    {
        var a = await MachineA();

        var env = await Json(await _http.GetAsync($"/api/agents/environment?environmentId={a}"));
        Assert.Equal("host", env.GetProperty("mode").GetString());
        Assert.Contains("host agent", env.GetProperty("description").GetString());
        var server = await Json(await _http.GetAsync("/api/agents/environment"));
        Assert.Equal("local", server.GetProperty("mode").GetString());

        var agents = await Json(await _http.GetAsync($"/api/agents?environmentId={a}"));
        Assert.Equal("claude-code", agents[0].GetProperty("name").GetString());
    }

    [PosixFact]
    public async Task An_offline_machine_shows_the_real_reason_on_the_agents_screen()
    {
        var offline = await CreateEnvironment("maquina-C", "http://127.0.0.1:1", TokenA);

        var agents = await Json(await _http.GetAsync($"/api/agents?environmentId={offline}"));

        Assert.False(agents[0].GetProperty("installed").GetBoolean());
        Assert.Contains("inacessível", agents[0].GetProperty("detail").GetString());
    }

    /// <summary>Launcher de teste: roda local, mas cada "máquina" tem a própria pasta de trabalho (onde mora o arquivo de login).</summary>
    private sealed class Machine(string dir) : IProcessLauncher
    {
        private readonly LocalProcessLauncher _inner = new();
        public string Mode => "local";
        public string Description => dir;
        public Task<string> ScratchDirectoryAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(dir);
            return Task.FromResult(dir);
        }
        public Task<IRunningProcess> StartAsync(LaunchSpec spec, CancellationToken ct) => _inner.StartAsync(spec, ct);
        public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct) => _inner.DirectoryExistsAsync(path, ct);
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => _inner.CreateDirectoryAsync(path, ct);
        public Task DeleteDirectoryAsync(string path, CancellationToken ct) => _inner.DeleteDirectoryAsync(path, ct);
    }

    private sealed class TwoMachines(Guid a, Guid b, string temp) : IEnvironmentLaunchers
    {
        public EnvironmentTarget Default { get; } = new(new Machine(Path.Combine(temp, "m-default")), null, null, "Padrão");
        public Task<EnvironmentTarget> ResolveAsync(Guid? environmentId, CancellationToken ct)
        {
            if (environmentId is null) return Task.FromResult(Default);
            if (environmentId == a) return Task.FromResult(new EnvironmentTarget(new Machine(Path.Combine(temp, "m-a")), null, a, "A"));
            if (environmentId == b) return Task.FromResult(new EnvironmentTarget(new Machine(Path.Combine(temp, "m-b")), null, b, "B"));
            throw new EnvironmentUnavailableException("Ambiente inexistente.");
        }
        public EnvironmentTarget ForEnvironment(AgentForge.Domain.ExecutionEnvironment environment) => Default;
    }

    [PosixFact]
    public async Task Agent_login_status_and_sessions_are_isolated_between_machines()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        // o estado "logado" é um arquivo RELATIVO: cada máquina o tem na sua pasta de trabalho
        var service = new AgentCliService(new AgentAdapterRegistry([new FakeAccountAdapter("logado")]),
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), new TwoMachines(a, b, _temp), NullLogger<AgentCliService>.Instance);

        Assert.False((await service.GetAsync(a, "fake-cli", force: true, default)).Authenticated);
        Assert.False((await service.GetAsync(b, "fake-cli", force: true, default)).Authenticated);

        var login = await service.StartLoginAsync(a, "fake-cli", default);
        Assert.NotEmpty(login.SessionId);

        // outra máquina (e o servidor) não enxergam nem conduzem a sessão de login da máquina A
        Assert.Throws<AgentNotFoundException>(() => service.GetLogin(b, "fake-cli", login.SessionId));
        Assert.Throws<AgentNotFoundException>(() => service.GetLogin(null, "fake-cli", login.SessionId));
        await Assert.ThrowsAsync<AgentNotFoundException>(() => service.SubmitCodeAsync(b, "fake-cli", login.SessionId, FakeAccountAdapter.RightCode, default));
        Assert.Throws<AgentNotFoundException>(() => service.CancelLogin(b, "fake-cli", login.SessionId));

        var done = await service.SubmitCodeAsync(a, "fake-cli", login.SessionId, FakeAccountAdapter.RightCode, default);
        Assert.Equal(LoginState.Succeeded, done.State);

        Assert.True((await service.GetAsync(a, "fake-cli", force: false, default)).Authenticated);
        Assert.False((await service.GetAsync(b, "fake-cli", force: false, default)).Authenticated);   // cache e login separados
        Assert.False((await service.GetAsync(null, "fake-cli", force: true, default)).Authenticated);

        // sair numa máquina não desloga as outras
        var loginB = await service.StartLoginAsync(b, "fake-cli", default);
        await service.SubmitCodeAsync(b, "fake-cli", loginB.SessionId, FakeAccountAdapter.RightCode, default);
        Assert.True((await service.GetAsync(b, "fake-cli", force: true, default)).Authenticated);
        Assert.False((await service.LogoutAsync(a, "fake-cli", default)).Authenticated);
        Assert.True((await service.GetAsync(b, "fake-cli", force: true, default)).Authenticated);
    }

    [PosixFact]
    public async Task A_new_login_on_one_machine_does_not_cancel_the_login_of_another()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var service = new AgentCliService(new AgentAdapterRegistry([new FakeAccountAdapter("logado")]),
            _factory.Services.GetRequiredService<IServiceScopeFactory>(), new TwoMachines(a, b, _temp), NullLogger<AgentCliService>.Instance);

        var inA = await service.StartLoginAsync(a, "fake-cli", default);
        var inB = await service.StartLoginAsync(b, "fake-cli", default);

        Assert.Equal(LoginState.WaitingForCode, service.GetLogin(a, "fake-cli", inA.SessionId).State);
        Assert.Equal(LoginState.WaitingForCode, service.GetLogin(b, "fake-cli", inB.SessionId).State);
        service.CancelLogin(a, "fake-cli", inA.SessionId);
        service.CancelLogin(b, "fake-cli", inB.SessionId);
    }
}
