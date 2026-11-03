using System.Diagnostics;
using System.Text;
using AgentForge.Domain;
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
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

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
            using var process = Start(adapter, request);
            using var killOnCancel = linked.Token.Register(() => Kill(process));

            var errTask = ReadStderrAsync(process, stderr);
            var inTask = WritePromptAsync(process, request.Prompt);

            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(linked.Token)) is not null)
            {
                stdout.Add(line);
                if (adapter.ParseEvent(line) is { } data)
                    await publisher.PublishAsync(
                        new AgentEvent(run.Id, request.DemandId, request.WorkItemId, request.Role, data.Kind, data.Text, DateTime.UtcNow),
                        CancellationToken.None);
            }

            await process.WaitForExitAsync(linked.Token);
            // stderr costuma fechar junto com o processo; não espera para sempre se algum neto segurar o pipe.
            await Task.WhenAny(Task.WhenAll(errTask, inTask), Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
            exitCode = process.ExitCode;
            result = adapter.ParseResult(stdout, process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            failure = timedOut ? $"Tempo limite de {(request.Timeout ?? DefaultTimeout).TotalSeconds:0}s excedido." : "Execução cancelada.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
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
        await db.SaveChangesAsync(CancellationToken.None);

        return new AgentRunResult(run.Id, status, output, result?.Json, run.CostUsd, clock.Elapsed, exitCode);
    }

    private static Process Start(ICliAgentAdapter adapter, AgentRequest request)
    {
        var psi = new ProcessStartInfo(adapter.Executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in adapter.BuildArguments(request)) psi.ArgumentList.Add(arg);
        return Process.Start(psi) ?? throw new InvalidOperationException($"Não foi possível iniciar '{adapter.Executable}'.");
    }

    private static async Task WritePromptAsync(Process process, string prompt)
    {
        try
        {
            await process.StandardInput.WriteAsync(prompt);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // o processo terminou (ou foi morto) antes de ler o prompt inteiro
        }
    }

    private static async Task ReadStderrAsync(Process process, Queue<string> tail)
    {
        string? line;
        while ((line = await process.StandardError.ReadLineAsync()) is not null)
        {
            tail.Enqueue(line);
            if (tail.Count > StderrTailLines) tail.Dequeue();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // já terminou
        }
    }
}
