using System.Diagnostics;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentForge.Tests;

/// <summary>
/// O host agent de verdade (Kestrel em porta aleatória) com o RemoteProcessLauncher falando HTTP com ele: é o que a
/// API no Docker faz com o claude/git do computador. Só roda em Linux/macOS (usa `sh`).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HostAgentTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Token = "token-de-teste-com-mais-de-24-caracteres";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-host-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_temp, "repos");
    private WebApplication? _app;
    private string _url = "";
    private RemoteProcessLauncher _launcher = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        if (OperatingSystem.IsWindows()) return;
        _app = AgentForge.Tests.Orchestrator.HostAgentFactory.Create(Root, Token);
        await _app.StartAsync();
        _url = _app.Urls.First();
        _launcher = new RemoteProcessLauncher(new HttpClient(), _url, Token);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
    }

    private LaunchSpec Sh(string script, string? stdin = null, bool closeStdin = true, string? cwd = null) =>
        new("sh", ["-c", script], cwd ?? Root, stdin, closeStdin);

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [PosixFact]
    public async Task Reports_host_info_and_rejects_a_wrong_token()
    {
        var info = await _launcher.InfoAsync(default);
        Assert.Contains(Root, info.Roots.Select(r => r.Replace('\\', '/')));
        Assert.Contains("sh", info.Executables);
        Assert.Equal("host", _launcher.Mode);

        var wrong = new RemoteProcessLauncher(new HttpClient(), _url, "outro-token-que-nao-e-o-certo-123");
        var ex = await Assert.ThrowsAsync<ProcessStartException>(() => wrong.StartAsync(Sh("echo oi"), default));
        Assert.Contains("recusou o token", ex.Message);
        await Assert.ThrowsAsync<ProcessStartException>(() => wrong.InfoAsync(default));
    }

    [PosixFact]
    public async Task Streams_stdout_stderr_and_exit_code()
    {
        var result = await _launcher.RunAsync(Sh("echo um; echo erro >&2; echo dois; exit 3"), TimeSpan.FromSeconds(20), default);

        Assert.True(result.Started);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("um\ndois\n", result.Stdout);
        Assert.Equal("erro\n", result.Stderr);
    }

    [PosixFact]
    public async Task Sends_the_initial_stdin_and_supports_interactive_input_afterwards()
    {
        var piped = await _launcher.RunAsync(Sh("cat", stdin: "prompt longo\ncom linhas"), TimeSpan.FromSeconds(20), default);
        Assert.Equal("prompt longo\ncom linhas\n", piped.Stdout);

        // login interativo: o processo espera o código depois de iniciar
        await using var process = await _launcher.StartAsync(Sh("echo 'abra o link'; read code; echo \"recebi:$code\"", closeStdin: false), default);
        Assert.Equal("abra o link", await process.ReadStdoutLineAsync(default));
        await process.WriteStdinAsync("codigo-123\n");
        Assert.Equal("recebi:codigo-123", await process.ReadStdoutLineAsync(default));
        Assert.Equal(0, await process.WaitForExitAsync(default));
    }

    [PosixFact]
    public async Task Output_arrives_while_the_process_is_still_running()
    {
        await using var process = await _launcher.StartAsync(Sh("echo primeira; sleep 5; echo segunda"), default);

        var clock = Stopwatch.StartNew();
        var first = await process.ReadStdoutLineAsync(default);

        Assert.Equal("primeira", first);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), "a saída deve chegar sem esperar o fim do processo");
        process.Kill();
    }

    [PosixFact]
    public async Task Kill_and_client_disconnect_stop_the_process_tree()
    {
        // 1) Kill explícito
        await using (var process = await _launcher.StartAsync(Sh("sleep 60 & echo $!; wait"), default))
        {
            var pid = int.Parse((await process.ReadStdoutLineAsync(default))!);
            process.Kill();
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(!IsAlive(pid)), 10));
        }

        // 2) a API some (queda de conexão / dispose): o host agent mata o que ficou
        int orphan;
        var detached = await _launcher.StartAsync(Sh("sleep 60 & echo $!; wait"), default);
        orphan = int.Parse((await detached.ReadStdoutLineAsync(default))!);
        await detached.DisposeAsync();
        Assert.True(await OrchestratorHarness.UntilAsync(() => Task.FromResult(!IsAlive(orphan)), 10));
    }

    [PosixFact]
    public async Task Only_allowed_executables_and_folders_are_accepted()
    {
        var notAllowed = await Assert.ThrowsAsync<ProcessStartException>(() => _launcher.StartAsync(new LaunchSpec("rm", ["-rf", Root], Root), default));
        Assert.Contains("não é permitido", notAllowed.Message);

        var outside = await Assert.ThrowsAsync<ProcessStartException>(() => _launcher.StartAsync(Sh("echo x", cwd: "/etc"), default));
        Assert.Contains("fora das pastas permitidas", outside.Message);

        var traversal = await Assert.ThrowsAsync<ProcessStartException>(() => _launcher.StartAsync(Sh("echo x", cwd: Root + "/../.."), default));
        Assert.Contains("fora das pastas permitidas", traversal.Message);

        var missing = await Assert.ThrowsAsync<ProcessStartException>(() => _launcher.StartAsync(Sh("echo x", cwd: Root + "/nao-existe"), default));
        Assert.Contains("inexistente", missing.Message);
    }

    [PosixFact]
    public async Task File_operations_are_limited_to_the_allowed_folders()
    {
        var dir = Root + "/proj/repo";
        Assert.False(await _launcher.DirectoryExistsAsync(dir, default));
        await _launcher.CreateDirectoryAsync(dir, default);
        Assert.True(await _launcher.DirectoryExistsAsync(dir, default));
        await File.WriteAllTextAsync(Path.Combine(dir, "x.txt"), "x");

        await _launcher.DeleteDirectoryAsync(dir, default);
        Assert.False(Directory.Exists(dir));

        await Assert.ThrowsAsync<ProcessStartException>(() => _launcher.DeleteDirectoryAsync(Root, default));     // nunca a raiz
        await Assert.ThrowsAsync<ProcessStartException>(() => _launcher.DeleteDirectoryAsync("/tmp", default));
        await Assert.ThrowsAsync<ProcessStartException>(() => _launcher.CreateDirectoryAsync(Root + "/../fora", default));
        await Assert.ThrowsAsync<ProcessStartException>(() => _launcher.DirectoryExistsAsync("/etc", default));
        Assert.True(Directory.Exists(Root));
    }

    [PosixFact]
    public async Task Unreachable_host_agent_is_reported_clearly()
    {
        var offline = new RemoteProcessLauncher(new HttpClient(), "http://127.0.0.1:1", Token);

        var ex = await Assert.ThrowsAsync<ProcessStartException>(() => offline.StartAsync(Sh("echo oi"), default));

        Assert.Contains("inacessível", ex.Message);
    }

    [PosixFact]
    public async Task Agent_runner_works_through_the_host_agent()
    {
        await using var db = await Db.CreateAsync(pg.NewConnectionString());
        var events = new CollectingPublisher();
        var runner = new CliAgentRunner(
            new AgentAdapterRegistry([new FakeCliAgentAdapter("""read p; echo "evt:prompt=$p"; echo 'result:{"ok":true}'""")]),
            db, events, _launcher, NullLogger<CliAgentRunner>.Instance);

        var result = await runner.RunAsync(new AgentRequest(AgentRole.Dev, "olá do container", Root, "fake"), default);

        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal("""{"ok":true}""", result.Json);
        Assert.Equal("prompt=olá do container", events.Events.Single().Text);

        // cancelamento: mata o processo no host
        using var cts = new CancellationTokenSource();
        var runner2 = new CliAgentRunner(new AgentAdapterRegistry([new FakeCliAgentAdapter("""echo "evt:$$"; exec sleep 60""")]),
            db, events, _launcher, NullLogger<CliAgentRunner>.Instance);
        var running = runner2.RunAsync(new AgentRequest(AgentRole.Dev, "x", Root, "fake"), cts.Token);
        await Task.Delay(1000);
        cts.Cancel();
        Assert.Equal(AgentRunStatus.Cancelled, (await running.WaitAsync(TimeSpan.FromSeconds(15))).Status);
    }

    [PosixFact]
    public async Task Workspaces_and_git_run_on_the_host_side()
    {
        var origin = await GitOrigin.CreateAsync(_temp);
        var manager = new WorkspaceManager(Options.Create(new WorkspaceOptions { Root = Root }), _launcher);
        var project = new Project { Name = "P", RepositoryUrl = origin, BaseBranch = "main" };
        var taskId = Guid.NewGuid();

        var ws = await manager.CreateWorktreeAsync(project, taskId, default);
        await File.WriteAllTextAsync(Path.Combine(ws.Path, "novo.txt"), "do host\n");
        await GitCli.RunAsync(_launcher, ws.Path, ["add", "."], default);
        await GitCli.RunAsync(_launcher, ws.Path, ["commit", "-m", "feat"], default);

        Assert.Equal($"{Root}/{project.Id}/worktrees/{taskId}", ws.Path);
        Assert.Contains("+do host", await manager.GetDiffAsync(project, taskId, default));
        await Assert.ThrowsAsync<GitException>(() => GitCli.RunAsync(_launcher, ws.Path, ["push", "origin", "HEAD:main"], default));

        await manager.RemoveWorktreeAsync(project, taskId, deleteBranch: false, default);
        Assert.False(Directory.Exists(ws.Path));
    }

    [PosixFact]
    public async Task Login_by_link_works_through_the_host_agent()
    {
        var state = Path.Combine(_temp, "logado");
        var fake = new FakeAccountAdapter(state);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AgentForge.Infrastructure.Persistence.AppDbContext>(o => o.UseNpgsql(pg.NewConnectionString()));
        services.AddSingleton<ICliAgentAdapter>(fake);
        services.AddSingleton<AgentAdapterRegistry>();
        services.AddSingleton<IAgentEventPublisher, NullAgentEventPublisher>();
        services.AddSingleton<IProcessLauncher>(_launcher);
        services.AddScoped<IAgentRunner, CliAgentRunner>();
        services.AddSingleton<AgentCliService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<AgentCliService>();

        Assert.Equal("host", service.Environment.Mode);
        Assert.False((await service.GetAsync("fake-cli", force: true, default)).Authenticated);

        var started = await service.StartLoginAsync("fake-cli", default);
        Assert.Equal("https://exemplo.local/auth?x=1&y=2", started.Url);
        var done = await service.SubmitCodeAsync("fake-cli", started.SessionId, FakeAccountAdapter.RightCode, default);

        Assert.Equal(LoginState.Succeeded, done.State);
        Assert.True((await service.GetAsync("fake-cli", force: true, default)).Authenticated);
    }
}
