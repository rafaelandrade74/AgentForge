using System.Diagnostics;
using AgentForge.Domain;
using AgentForge.Orchestrator.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.Tests;

/// <summary>Os testes do runner usam `sh` como CLI falso; só rodam em Linux/macOS (CI e container).</summary>
public sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requer sh (Linux/macOS).";
    }
}

/// <summary>Adapter falso: executa um script de shell. "evt:x" vira evento; "result:x" é o resultado.</summary>
public class FakeCliAgentAdapter(string script, string executable = "sh") : ICliAgentAdapter
{
    public string Name => "fake";
    public string Executable => executable;
    public IReadOnlyList<string> BuildArguments(AgentRequest request) => ["-c", script];

    public AgentEventData? ParseEvent(string line) =>
        line.StartsWith("evt:") ? new("log", line[4..]) : null;

    public AgentResult ParseResult(IReadOnlyList<string> stdoutLines, int exitCode)
    {
        var text = stdoutLines.LastOrDefault(l => l.StartsWith("result:"))?[7..] ?? "";
        return new AgentResult(text, JsonBlock.ExtractLast(text), 0.5m, exitCode != 0);
    }
}

public class CollectingPublisher : IAgentEventPublisher
{
    private readonly List<AgentEvent> _events = [];
    public TaskCompletionSource FirstEvent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<AgentEvent> Events { get { lock (_events) return _events.ToList(); } }

    public Task PublishAsync(AgentEvent evt, CancellationToken ct = default)
    {
        lock (_events) _events.Add(evt);
        FirstEvent.TrySetResult();
        return Task.CompletedTask;
    }
}

[Collection(PostgresCollection.Name)]
public class AgentRunnerTests(PostgresFixture pg)
{
    private static AgentRequest Request(string prompt = "oi", TimeSpan? timeout = null) =>
        new(AgentRole.Dev, prompt, Path.GetTempPath(), "fake", Timeout: timeout);

    private async Task<(CliAgentRunner Runner, CollectingPublisher Events, Infrastructure.Persistence.AppDbContext Db)> Create(
        FakeCliAgentAdapter adapter)
    {
        var db = await Db.CreateAsync(pg.NewConnectionString());
        var events = new CollectingPublisher();
        return (new CliAgentRunner(new AgentAdapterRegistry([adapter]), db, events, NullLogger<CliAgentRunner>.Instance), events, db);
    }

    [PosixFact]
    public async Task Fake_adapter_runs_without_a_real_cli_and_persists_the_run()
    {
        var (runner, events, db) = await Create(new FakeCliAgentAdapter(
            """read p; echo "evt:prompt=$p"; echo "evt:pronto"; echo 'result:{"status":"approved"}'"""));

        var result = await runner.RunAsync(Request("olá"), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Equal("""{"status":"approved"}""", result.Json);
        Assert.Equal(["prompt=olá", "pronto"], events.Events.Select(e => e.Text));
        Assert.All(events.Events, e => Assert.Equal(result.RunId, e.RunId));

        var saved = await db.AgentRuns.SingleAsync();
        Assert.Equal(AgentRunStatus.Succeeded, saved.Status);
        Assert.Equal("olá", saved.Prompt);
        Assert.Equal(0.5m, saved.CostUsd);
        Assert.True(saved.DurationMs >= 0 && saved.FinishedAt is not null);
    }

    [PosixFact]
    public async Task Non_zero_exit_fails_and_keeps_stderr()
    {
        var (runner, _, db) = await Create(new FakeCliAgentAdapter("echo 'deu ruim' >&2; exit 3"));

        var result = await runner.RunAsync(Request(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("deu ruim", (await db.AgentRuns.SingleAsync()).Output);
    }

    [PosixFact]
    public async Task Cancellation_kills_the_child_process()
    {
        var (runner, events, db) = await Create(new FakeCliAgentAdapter("""echo "evt:$$"; exec sleep 60"""));
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(Request(), cts.Token);
        await events.FirstEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pid = int.Parse(events.Events[0].Text);
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AgentRunStatus.Cancelled, result.Status);
        Assert.True(result.Duration < TimeSpan.FromSeconds(10));
        Assert.False(IsAlive(pid));
        Assert.Equal(AgentRunStatus.Cancelled, (await db.AgentRuns.SingleAsync()).Status);
    }

    [PosixFact]
    public async Task Cancellation_kills_grandchild_processes_too()
    {
        var (runner, events, _) = await Create(new FakeCliAgentAdapter("""sleep 60 & echo "evt:$!"; wait"""));
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(Request(), cts.Token);
        await events.FirstEvent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pid = int.Parse(events.Events[0].Text);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(IsAlive(pid));
    }

    [PosixFact]
    public async Task Timeout_fails_the_run()
    {
        var (runner, _, db) = await Create(new FakeCliAgentAdapter("exec sleep 60"));

        var result = await runner.RunAsync(Request(timeout: TimeSpan.FromSeconds(1)), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Contains("Tempo limite", (await db.AgentRuns.SingleAsync()).Output);
    }

    [Fact]
    public async Task Missing_executable_fails_instead_of_throwing()
    {
        var (runner, _, _) = await Create(new FakeCliAgentAdapter("", "executavel-que-nao-existe"));

        var result = await runner.RunAsync(Request(), CancellationToken.None);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Contains("executavel-que-nao-existe", result.Output);
    }

    [Fact]
    public void Registry_rejects_unknown_adapter()
    {
        var registry = new AgentAdapterRegistry([new FakeCliAgentAdapter("")]);
        Assert.Throws<InvalidOperationException>(() => registry.Get("codex"));
        Assert.Equal("fake", registry.Get("FAKE").Name);
    }

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
}
