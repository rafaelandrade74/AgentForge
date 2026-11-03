using System.Diagnostics;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace AgentForge.Orchestrator.Agents;

/// <summary>
/// Executa qualquer agente via CLI: inicia o binário no diretório de trabalho, envia o prompt pelo stdin,
/// lê o stdout linha a linha publicando eventos, aplica timeout e cancelamento (matando a árvore de
/// processos) e persiste o AgentRun.
/// </summary>
public class CliAgentRunner(
    AgentAdapterRegistry adapters,
    AppDbContext db,
    IAgentEventPublisher publisher,
    IProcessLauncher launcher,
    ILogger<CliAgentRunner> logger) : IAgentRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);
    private const int StderrTailLines = 50;

    public async Task<AgentRunResult> RunAsync(AgentRequest request, CancellationToken ct)
    {
        var adapter = adapters.Get(request.AdapterName);
        var run = new AgentRun
        {
            DemandId = request.DemandId,
            WorkItemId = request.WorkItemId,
            Role = request.Role,
            Prompt = request.Prompt,
            Status = AgentRunStatus.Running,
        };
        if (!request.Ephemeral)
        {
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        Task Emit(string kind, string text) => request.Ephemeral ? Task.CompletedTask : publisher.PublishAsync(
            new AgentEvent(run.Id, request.ProjectId, request.DemandId, request.WorkItemId, request.Role, kind, text, DateTime.UtcNow),
            CancellationToken.None);
        await Emit("run_started", request.Role.ToString());

        var clock = Stopwatch.StartNew();
        var stdout = new List<string>();
        var stderr = new Queue<string>();
        int? exitCode = null;
        AgentResult? result = null;
        string? failure = null;
        var timedOut = false;

        using var timeoutCts = new CancellationTokenSource(request.Timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            // o prompt vai pelo stdin (e o stdin fecha); o processo roda onde o launcher manda (aqui ou no computador)
            await using var process = await launcher.StartAsync(
                new LaunchSpec(adapter.Executable, adapter.BuildArguments(request), request.WorkingDirectory, request.Prompt), linked.Token);
            using var killOnCancel = linked.Token.Register(process.Kill);

            var errTask = ReadStderrAsync(process, stderr);

            string? line;
            while ((line = await process.ReadStdoutLineAsync(linked.Token)) is not null)
            {
                stdout.Add(line);
                if (adapter.ParseEvent(line) is { } data) await Emit(data.Kind, data.Text);
            }

            exitCode = await process.WaitForExitAsync(linked.Token);
            // stderr costuma fechar junto com o processo; não espera para sempre se algum neto segurar o pipe.
            await Task.WhenAny(errTask, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
            result = adapter.ParseResult(stdout, exitCode.Value);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            failure = timedOut ? $"Tempo limite de {(request.Timeout ?? DefaultTimeout).TotalSeconds:0}s excedido." : "Execução cancelada.";
        }
        catch (Exception ex) when (ex is ProcessStartException or InvalidOperationException or IOException)
        {
            logger.LogError(ex, "Falha ao executar o CLI do agente {Adapter}", adapter.Name);
            failure = $"Falha ao executar '{adapter.Executable}': {ex.Message}";
        }

        clock.Stop();
        var status =
            failure is not null ? (timedOut || !ct.IsCancellationRequested ? AgentRunStatus.Failed : AgentRunStatus.Cancelled)
            : result!.IsError ? AgentRunStatus.Failed
            : AgentRunStatus.Succeeded;

        var output = result?.Text ?? "";
        if (failure is not null || status == AgentRunStatus.Failed)
        {
            var tail = string.Join(Environment.NewLine, stderr);
            output = string.Join(Environment.NewLine, new[] { failure, output, tail }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        run.Status = status;
        run.Output = output;
        run.CostUsd = result?.CostUsd ?? 0;
        run.DurationMs = clock.ElapsedMilliseconds;
        run.FinishedAt = DateTime.UtcNow;
        if (!request.Ephemeral) await db.SaveChangesAsync(CancellationToken.None);
        await Emit("run_finished", status.ToString());

        return new AgentRunResult(run.Id, status, output, result?.Json, run.CostUsd, clock.Elapsed, exitCode);
    }

    private static async Task ReadStderrAsync(IRunningProcess process, Queue<string> tail)
    {
        string? line;
        while ((line = await process.ReadStderrLineAsync(CancellationToken.None)) is not null)
        {
            tail.Enqueue(line);
            if (tail.Count > StderrTailLines) tail.Dequeue();
        }
    }
}
