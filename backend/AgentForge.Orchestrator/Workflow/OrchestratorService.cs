using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.Orchestrator.Workflow;

/// <summary>
/// Consome a fila (System.Threading.Channels) com até MaxConcurrency trabalhadores e avança cada WorkItem pela
/// máquina de estados. O mesmo item nunca é processado por dois trabalhadores ao mesmo tempo; um aviso que chega
/// enquanto ele está na fila ou rodando faz uma nova passada ao terminar.
/// </summary>
public sealed class OrchestratorService(
    IServiceScopeFactory scopes,
    IOptions<OrchestratorOptions> options,
    ILogger<OrchestratorService> logger) : BackgroundService, IWorkQueue
{
    private readonly record struct Work(bool IsDemand, Guid Id);

    private sealed record Active(CancellationTokenSource Cts, TaskCompletionSource Done);

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>();
    private readonly object _gate = new();
    private readonly HashSet<Work> _pending = [];   // na fila ou em execução
    private readonly HashSet<Work> _again = [];     // avisados enquanto pendentes
    private readonly ConcurrentDictionary<Guid, Active> _active = new();

    // ------------------------------------------------------------------ IWorkQueue

    public void EnqueueWorkItem(Guid id) => Enqueue(new Work(false, id));
    public void EnqueueDemand(Guid id) => Enqueue(new Work(true, id));

    private void Enqueue(Work work)
    {
        lock (_gate)
        {
            if (!_pending.Add(work)) { _again.Add(work); return; }
        }
        _channel.Writer.TryWrite(work);
    }

    public async Task CancelRunningAsync(Guid workItemId)
    {
        if (!_active.TryGetValue(workItemId, out var active)) return;
        await active.Cts.CancelAsync();
        await active.Done.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    // ------------------------------------------------------------------ ciclo de vida

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            await ResumeAsync(stop);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha ao retomar o estado do orquestrador.");
        }

        var workers = Enumerable.Range(0, Math.Max(1, options.Value.MaxConcurrency)).Select(_ => WorkerAsync(stop));
        await Task.WhenAll(workers);
    }

    /// <summary>Depois de um restart: fecha execuções que ficaram Running e recoloca na fila tudo que estava em andamento.</summary>
    private async Task ResumeAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var orphans = await db.AgentRuns
            .Where(r => r.Status == AgentRunStatus.Running || r.Status == AgentRunStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AgentRunStatus.Failed)
                .SetProperty(r => r.Output, "Interrompida por reinício da API.")
                .SetProperty(r => r.FinishedAt, DateTime.UtcNow), ct);
        if (orphans > 0) logger.LogWarning("{Count} execução(ões) de agente interrompida(s) por reinício foram encerradas.", orphans);

        foreach (var id in await db.Demands.Where(d => d.Status == DemandStatus.Refining).Select(d => d.Id).ToListAsync(ct))
            EnqueueDemand(id);
        foreach (var id in await db.WorkItems.Where(w => WorkflowDefinition.Resumable.Contains(w.State)).Select(w => w.Id).ToListAsync(ct))
            EnqueueWorkItem(id);
    }

    private async Task WorkerAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var work in _channel.Reader.ReadAllAsync(stop))
            {
                try
                {
                    if (work.IsDemand) await RefineAsync(work.Id, stop);
                    else await ProcessItemAsync(work.Id, stop);
                }
                finally
                {
                    bool again;
                    lock (_gate)
                    {
                        again = _again.Remove(work);
                        if (!again) _pending.Remove(work);
                    }
                    if (again && !stop.IsCancellationRequested) _channel.Writer.TryWrite(work);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // desligando: o estado persistido permite retomar no próximo start
        }
    }

    private async Task RefineAsync(Guid demandId, CancellationToken stop)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DemandProcessor>().RefineAsync(demandId, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro ao refinar a demanda {Id}", demandId);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var updated = await db.Demands
                .Where(d => d.Id == demandId && d.Status == DemandStatus.Refining)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DemandStatus.Failed));
            if (updated > 0)
                await scope.ServiceProvider.GetRequiredService<IWorkflowEventPublisher>()
                    .PublishChangeAsync(await db.Demands.AsNoTracking().FirstAsync(d => d.Id == demandId));
        }
    }

    private async Task ProcessItemAsync(Guid id, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var active = new Active(cts, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _active[id] = active;
        try
        {
            while (true)
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<WorkItemProcessor>();
                if (await processor.StepAsync(id, cts.Token) == StepResult.Stop) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro inesperado ao processar o WorkItem {Id}", id);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WorkItemProcessor>().FailAsync(id, ex.Message);
        }
        finally
        {
            _active.TryRemove(id, out _);
            active.Done.TrySetResult();
        }
    }
}
