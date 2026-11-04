using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentForge.Domain;
using AgentForge.Execution;
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
///
/// Cada ambiente tem a sua concorrência máxima: o trabalho que não cabe fica estacionado no ambiente (sem prender um
/// trabalhador) e volta quando uma vaga abre. Se a máquina do ambiente não responde, o item não vira Blocked: espera com
/// backoff e é tentado de novo, mantendo o estado.
/// </summary>
public sealed class OrchestratorService(
    IServiceScopeFactory scopes,
    IOptions<OrchestratorOptions> options,
    ILogger<OrchestratorService> logger) : BackgroundService, IWorkQueue, IEnvironmentLoad
{
    private readonly record struct Work(bool IsDemand, Guid Id);

    private sealed record Active(CancellationTokenSource Cts, TaskCompletionSource Done);

    private readonly record struct Home(string Key, int Limit);

    private readonly Channel<Work> _channel = Channel.CreateUnbounded<Work>();
    private readonly object _gate = new();
    private readonly HashSet<Work> _pending = [];   // na fila ou em execução
    private readonly HashSet<Work> _again = [];     // avisados enquanto pendentes
    private readonly ConcurrentDictionary<Guid, Active> _active = new();
    private readonly EnvironmentGate<Work> _slots = new();
    private readonly ConcurrentDictionary<Work, WaitingEntry> _waiting = new();
    private readonly ConcurrentDictionary<Work, int> _attempts = new();
    private CancellationToken _stop;

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

    // ------------------------------------------------------------------ IEnvironmentLoad

    public IReadOnlyList<EnvironmentSlotInfo> Slots() => _slots.Snapshot();
    public IReadOnlyList<WaitingEntry> Waiting() => _waiting.Values.OrderBy(w => w.RetryAt).ToList();

    // ------------------------------------------------------------------ ciclo de vida

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        _stop = stop;
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
                var home = await HomeAsync(work, stop);
                if (!_slots.TryEnter(home.Key, home.Limit, work)) continue;   // estacionado: volta quando abrir vaga neste ambiente

                TimeSpan? retryAfter = null;
                try
                {
                    retryAfter = work.IsDemand ? await RefineAsync(work, stop) : await ProcessItemAsync(work, stop);
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
                    else if (retryAfter is { } delay) ScheduleRetry(work, delay);

                    foreach (var parked in _slots.Leave(home.Key)) _channel.Writer.TryWrite(parked);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // desligando: o estado persistido permite retomar no próximo start
        }
    }

    /// <summary>Ambiente "de casa" do trabalho: o do projeto (onde mora o worktree), com o limite dele. Falha de leitura = ambiente padrão.</summary>
    private async Task<Home> HomeAsync(Work work, CancellationToken ct)
    {
        var fallback = new Home(EnvironmentKeys.Default, Math.Max(1, options.Value.MaxConcurrency));
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var projectId = work.IsDemand
                ? await db.Demands.Where(d => d.Id == work.Id).Select(d => (Guid?)d.ProjectId).FirstOrDefaultAsync(ct)
                : await db.WorkItems.Where(w => w.Id == work.Id)
                    .Join(db.Demands, w => w.DemandId, d => d.Id, (w, d) => (Guid?)d.ProjectId).FirstOrDefaultAsync(ct);
            if (projectId is null) return fallback;

            var environmentId = await db.Projects.Where(p => p.Id == projectId).Select(p => p.EnvironmentId).FirstOrDefaultAsync(ct);
            if (environmentId is null) return fallback;
            var limit = await db.ExecutionEnvironments.Where(e => e.Id == environmentId).Select(e => (int?)e.MaxConcurrency).FirstOrDefaultAsync(ct);
            return limit is null ? fallback : new Home(environmentId.Value.ToString(), limit.Value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Não foi possível ler o ambiente do trabalho {Id}; usando o padrão.", work.Id);
            return fallback;
        }
    }

    // ------------------------------------------------------------------ máquina fora do ar: espera e tenta de novo

    private TimeSpan NextDelay(Work work)
    {
        var attempt = _attempts.AddOrUpdate(work, 1, (_, n) => n + 1);
        var seconds = options.Value.EnvironmentRetryInitialSeconds * Math.Pow(2, Math.Min(attempt - 1, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, options.Value.EnvironmentRetryMaxSeconds));
    }

    private async Task<TimeSpan> DeferAsync(Work work, string reason)
    {
        var delay = NextDelay(work);
        _waiting[work] = new WaitingEntry(work.IsDemand, work.Id, reason, DateTime.UtcNow + delay, _attempts.GetValueOrDefault(work));
        logger.LogWarning("{Kind} {Id} aguarda o ambiente voltar (tentativa {Attempt}, próxima em {Delay:0}s): {Reason}",
            work.IsDemand ? "Demanda" : "Tarefa", work.Id, _attempts.GetValueOrDefault(work), delay.TotalSeconds, reason);
        if (!work.IsDemand)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var message = reason.Length > 480 ? reason[..480] + "…" : reason;
            var updated = await db.WorkItems.Where(w => w.Id == work.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.WaitingReason, "Aguardando o ambiente voltar: " + message));
            if (updated > 0)
                await scope.ServiceProvider.GetRequiredService<IWorkflowEventPublisher>().PublishChangeAsync(await ProjectIdOfAsync(db, work.Id), await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == work.Id));
        }
        return delay;
    }

    private static async Task<Guid> ProjectIdOfAsync(AppDbContext db, Guid workItemId) =>
        await db.WorkItems.Where(w => w.Id == workItemId).Join(db.Demands, w => w.DemandId, d => d.Id, (w, d) => d.ProjectId).FirstAsync();

    private void ScheduleRetry(Work work, TimeSpan delay)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _stop);
                Enqueue(work);
            }
            catch (OperationCanceledException)
            {
                // desligando: ResumeAsync recoloca o item na fila no próximo start
            }
        });
    }

    /// <summary>Ao recomeçar um trabalho que esperava: tira o aviso de espera (ele volta se a máquina continuar fora).</summary>
    private async Task StartedAsync(Work work)
    {
        if (!_waiting.TryRemove(work, out _) || work.IsDemand) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.WorkItems.Where(w => w.Id == work.Id).ExecuteUpdateAsync(s => s.SetProperty(w => w.WaitingReason, (string?)null));
    }

    private static bool IsEnvironmentDown(Exception ex) => ex is EnvironmentDeferredException or HostUnreachableException;

    // ------------------------------------------------------------------ processamento

    private async Task<TimeSpan?> RefineAsync(Work work, CancellationToken stop)
    {
        var demandId = work.Id;
        try
        {
            await StartedAsync(work);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DemandProcessor>().RefineAsync(demandId, stop);
            _attempts.TryRemove(work, out _);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (IsEnvironmentDown(ex))
        {
            return await DeferAsync(work, ex.Message);
        }
        catch (Exception ex)
        {
            _attempts.TryRemove(work, out _);
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
        return null;
    }

    private async Task<TimeSpan?> ProcessItemAsync(Work work, CancellationToken stop)
    {
        var id = work.Id;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var active = new Active(cts, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _active[id] = active;
        try
        {
            await StartedAsync(work);
            while (true)
            {
                using var scope = scopes.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<WorkItemProcessor>();
                if (await processor.StepAsync(id, cts.Token) == StepResult.Stop) break;
            }
            _attempts.TryRemove(work, out _);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsEnvironmentDown(ex))
        {
            return await DeferAsync(work, ex.Message);
        }
        catch (Exception ex)
        {
            _attempts.TryRemove(work, out _);
            logger.LogError(ex, "Erro inesperado ao processar o WorkItem {Id}", id);
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WorkItemProcessor>().FailAsync(id, ex.Message);
        }
        finally
        {
            _active.TryRemove(id, out _);
            active.Done.TrySetResult();
        }
        return null;
    }
}

public static class EnvironmentKeys
{
    /// <summary>Chave do ambiente padrão do servidor (projetos sem ambiente próprio).</summary>
    public const string Default = "default";
}
