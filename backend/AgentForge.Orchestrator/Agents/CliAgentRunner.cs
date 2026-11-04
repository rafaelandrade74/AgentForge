using System.Diagnostics;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Environments;
using Microsoft.EntityFrameworkCore;
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
    IEnvironmentLaunchers environments,
    ILogger<CliAgentRunner> logger) : IAgentRunner
{
    /// <summary>Um ambiente só, com o launcher dado (testes e instalações sem ambientes cadastrados).</summary>
    public CliAgentRunner(AgentAdapterRegistry adapters, AppDbContext db, IAgentEventPublisher publisher,
        IProcessLauncher launcher, ILogger<CliAgentRunner> logger)
        : this(adapters, db, publisher, new SingleEnvironment(launcher), logger) { }

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
        var environmentDown = false;
        var workingDirectory = request.WorkingDirectory;

        using var timeoutCts = new CancellationTokenSource(request.Timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            // o processo roda no ambiente do projeto (aqui, ou numa máquina com host agent); sem projeto, no padrão
            EnvironmentTarget target;
            ContainerSpec? container = null;
            if (request.EnvironmentOverride)
            {
                // papel movido para outra máquina: sem worktree do projeto, sem perfil de stack
                target = await environments.ResolveAsync(request.EnvironmentId, linked.Token);
                workingDirectory = await target.Launcher.ScratchDirectoryAsync(linked.Token);
            }
            else if (request.ProjectId is { } projectId)
            {
                var where = await db.Projects.AsNoTracking().Where(p => p.Id == projectId)
                    .Select(p => new { p.EnvironmentId, p.StackProfileId }).FirstOrDefaultAsync(linked.Token);
                target = await environments.ResolveAsync(where?.EnvironmentId, linked.Token);
                if (where?.StackProfileId is { } profileId)
                    container = await PrepareContainerAsync(target, projectId, profileId, ct);
            }
            else
            {
                target = await environments.ResolveAsync(request.EnvironmentId, linked.Token);
            }

            run.EnvironmentId = target.EnvironmentId;
            if (!request.Ephemeral) await db.SaveChangesAsync(CancellationToken.None);

            // o prompt vai pelo stdin (e o stdin fecha)
            await using var process = await target.Launcher.StartAsync(
                new LaunchSpec(adapter.Executable, adapter.BuildArguments(request), workingDirectory, request.Prompt, Container: container), linked.Token);
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
        catch (Exception ex) when (ex is ProcessStartException or EnvironmentUnavailableException or InvalidOperationException or IOException)
        {
            environmentDown = ex is HostUnreachableException;
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

        return new AgentRunResult(run.Id, status, output, result?.Json, run.CostUsd, clock.Elapsed, exitCode, environmentDown);
    }

    /// <summary>
    /// Projeto com perfil de stack: a execução vai para um container do perfil na máquina do ambiente (só host agent com Docker).
    /// Garante a imagem antes (constrói do Dockerfile na primeira vez; fora do tempo limite do agente).
    /// </summary>
    private async Task<ContainerSpec> PrepareContainerAsync(EnvironmentTarget target, Guid projectId, Guid profileId, CancellationToken ct)
    {
        if (target.Launcher is not RemoteProcessLauncher remote || target.WorkspaceRoot is not { Length: > 0 } root)
            throw new EnvironmentUnavailableException("O perfil de stack deste projeto exige um ambiente com host agent com Docker (escolha o ambiente no projeto).");
        var profile = await db.StackProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profileId, ct)
            ?? throw new EnvironmentUnavailableException("O perfil de stack deste projeto não existe mais. Escolha outro perfil no projeto.");

        var hasDockerfile = !string.IsNullOrWhiteSpace(profile.Dockerfile);
        var image = hasDockerfile ? ContainerRules.ImageTag(profile.Dockerfile) : profile.Image;
        await remote.EnsureImageAsync(image, hasDockerfile ? profile.Dockerfile : null, force: false, ct);
        var projectDir = root.Replace('\\', '/').TrimEnd('/') + "/" + projectId;
        return new ContainerSpec(image, null, profile.Network, profile.CpuLimit, profile.MemoryMb, projectDir);
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
