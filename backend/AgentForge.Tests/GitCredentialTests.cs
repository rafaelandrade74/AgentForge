using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Execution;
using AgentForge.Orchestrator.Workspaces;

namespace AgentForge.Tests;

/// <summary>Os testes de geração de chave precisam do ssh-keygen (use a imagem backend/Dockerfile.tests); sem ele, são pulados.</summary>
public sealed class SshFactAttribute : FactAttribute
{
    public SshFactAttribute()
    {
        if (!PostgresAvailability.IsAvailable) Skip = PostgresAvailability.SkipReason;
        else if (OperatingSystem.IsWindows() || !HasSshKeygen()) Skip = "Requer ssh-keygen (Linux/macOS com openssh-client).";
    }

    private static bool HasSshKeygen()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ssh-keygen", "-?") { RedirectStandardError = true, RedirectStandardOutput = true });
            p!.WaitForExit(3000);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed class GitCredentialServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "af-ssh-" + Guid.NewGuid().ToString("N"));
    private GitCredentialService Service(IProcessLauncher? launcher = null) =>
        new(launcher ?? new LocalProcessLauncher { GitSshDirectory = _dir }, new GitOptions { SshDir = _dir });

    private static string? SshEnv(ProcessStartInfo psi) => psi.Environment.TryGetValue("GIT_SSH_COMMAND", out var v) ? v : null;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [SshFact]
    public async Task Generates_an_ed25519_key_and_only_the_public_part_is_exposed()
    {
        var before = await Service().GetStatusAsync(default);
        Assert.True(before.Supported);
        Assert.False(before.HasKey);

        var status = await Service().GenerateAsync(overwrite: false, default);

        Assert.True(status.HasKey);
        Assert.StartsWith("ssh-ed25519 ", status.PublicKey);
        Assert.Contains("agentforge@", status.PublicKey);
        Assert.StartsWith("SHA256:", status.Fingerprint);
        Assert.NotNull(status.CreatedAt);
        Assert.DoesNotContain("PRIVATE KEY", System.Text.Json.JsonSerializer.Serialize(status));
        Assert.True(File.Exists(Path.Combine(_dir, "id_ed25519")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_dir, "id_ed25519")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));
    }

    [SshFact]
    public async Task Regenerating_needs_confirmation_and_replaces_the_key()
    {
        var first = await Service().GenerateAsync(false, default);

        var ex = await Assert.ThrowsAsync<GitCredentialException>(() => Service().GenerateAsync(overwrite: false, default));
        Assert.Contains("Já existe", ex.Message);
        Assert.Equal(first.PublicKey, (await Service().GetStatusAsync(default)).PublicKey);   // nada mudou

        var second = await Service().GenerateAsync(overwrite: true, default);
        Assert.NotEqual(first.PublicKey, second.PublicKey);
    }

    [SshFact]
    public async Task Deleting_removes_the_key_files()
    {
        await Service().GenerateAsync(false, default);

        await Service().DeleteAsync(default);

        Assert.False((await Service().GetStatusAsync(default)).HasKey);
        Assert.False(File.Exists(Path.Combine(_dir, "id_ed25519")));
        Assert.False(File.Exists(Path.Combine(_dir, "id_ed25519.pub")));
    }

    [Fact]
    public async Task In_host_agent_mode_the_feature_is_not_offered()
    {
        var remote = new RemoteProcessLauncher(new HttpClient(), "http://127.0.0.1:1", "token-qualquer-com-mais-de-24-chars");
        var service = Service(remote);

        var status = await service.GetStatusAsync(default);

        Assert.False(status.Supported);
        Assert.Contains("inacessível", status.Reason);
        await Assert.ThrowsAsync<GitCredentialException>(() => service.GenerateAsync(false, default));
        await Assert.ThrowsAsync<GitCredentialException>(() => service.DeleteAsync(default));
    }

    [Fact]
    public async Task Access_test_rejects_bad_input_and_reports_clear_results()
    {
        var service = Service();
        foreach (var bad in new[] { "", "  ", "--upload-pack=x", "git@host:a b.git", new string('x', 600) })
            Assert.False((await service.TestAccessAsync(bad, default)).Ok);

        // repositório local de verdade (sem SSH): o teste de acesso confirma e conta os branches
        var origin = await GitOrigin.CreateAsync(_dir + "-origin");
        try
        {
            var ok = await service.TestAccessAsync(origin, default);
            Assert.True(ok.Ok, ok.Message);
            Assert.Equal("Acesso confirmado (1 branch visível).", ok.Message);

            var missing = await service.TestAccessAsync(Path.Combine(_dir + "-origin", "nao-existe.git"), default);
            Assert.False(missing.Ok);
            Assert.NotEmpty(missing.Message);
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(_dir + "-origin", "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir + "-origin", recursive: true);
        }
    }

    [Fact]
    public async Task Git_uses_the_generated_key_only_when_it_exists_and_only_for_git()
    {
        Directory.CreateDirectory(_dir);
        Assert.Null(SshEnv(LocalProcessLauncher.StartInfo("git", ["status"], _dir, _dir)));

        await File.WriteAllTextAsync(Path.Combine(_dir, "id_ed25519"), "chave");
        var git = LocalProcessLauncher.StartInfo("git", ["status"], _dir, _dir).Environment["GIT_SSH_COMMAND"]!;

        Assert.Contains($"-i '{_dir.Replace('\\', '/')}/id_ed25519'", git);
        Assert.Contains("IdentitiesOnly=yes", git);
        Assert.Contains("StrictHostKeyChecking=accept-new", git);
        Assert.Contains("BatchMode=yes", git);
        Assert.Contains($"UserKnownHostsFile='{_dir.Replace('\\', '/')}/known_hosts'", git);
        // outros binários (o agente, por exemplo) não recebem a configuração, e sem pasta nada muda
        Assert.Null(SshEnv(LocalProcessLauncher.StartInfo("claude", ["-p"], _dir, _dir)));
        Assert.Null(SshEnv(LocalProcessLauncher.StartInfo("git", ["status"], _dir)));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class GitEndpointsTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "af-ssh-api-" + Guid.NewGuid().ToString("N"));
    private ApiFactory _factory = null!;
    private HttpClient _http = null!;

    private sealed class SshFactory(string cs, string dir) : ApiFactory(cs)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Git:SshDir", dir);
        }
    }

    public async Task InitializeAsync()
    {
        _factory = new SshFactory(pg.NewConnectionString(), _dir);
        _http = _factory.CreateClient();
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    [IntegrationFact]
    public async Task Requires_the_login_and_rejects_mcp_access_tokens()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/git/ssh")).StatusCode);

        var created = await _http.PostAsJsonAsync("/api/tokens", new { name = "mcp" });
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var mcp = _factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mcp.GetAsync("/api/git/ssh")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mcp.PostAsync("/api/git/ssh/generate", null)).StatusCode);
    }

    [IntegrationFact]
    public async Task Status_starts_without_key_and_validates_the_access_test_input()
    {
        var status = await _http.GetFromJsonAsync<JsonElement>("/api/git/ssh");
        Assert.True(status.GetProperty("supported").GetBoolean());
        Assert.False(status.GetProperty("hasKey").GetBoolean());

        var test = await _http.PostAsJsonAsync("/api/git/ssh/test", new { repositoryUrl = "" });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        Assert.False((await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
    }

    [SshFact]
    public async Task Generate_returns_the_public_key_never_the_private_one_and_delete_clears_it()
    {
        var generated = await _http.PostAsync("/api/git/ssh/generate", null);
        var body = await generated.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        Assert.DoesNotContain("PRIVATE KEY", body);
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.StartsWith("ssh-ed25519 ", json.GetProperty("publicKey").GetString());

        var again = await _http.PostAsync("/api/git/ssh/generate", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var replaced = await _http.PostAsJsonAsync("/api/git/ssh/generate", new { overwrite = true });
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

        var deleted = await _http.DeleteAsync("/api/git/ssh");
        Assert.False((await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasKey").GetBoolean());
    }

    [IntegrationFact]
    public async Task Host_agent_mode_reports_unsupported()
    {
        await using var host = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Agents:Launcher", "host");
            b.UseSetting("Agents:HostAgent:Url", "http://127.0.0.1:1");
            b.UseSetting("Agents:HostAgent:Token", "token-de-teste-com-mais-de-24-caracteres");
        });
        var client = host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        var status = await client.GetFromJsonAsync<JsonElement>("/api/git/ssh");

        Assert.False(status.GetProperty("supported").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/git/ssh/generate", null)).StatusCode);
    }
}
