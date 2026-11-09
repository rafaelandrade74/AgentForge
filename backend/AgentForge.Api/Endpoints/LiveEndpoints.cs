using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Workflow;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Api.Endpoints;

/// <summary>Um agente (CLI) de uma máquina: instalado, conectado, em uso e com limite de uso ou não. <c>Reachable</c> falso = a máquina não respondeu.</summary>
public record LiveAgentDto(Guid? EnvironmentId, string Environment, string Mode, string Agent, string DisplayName, bool Reachable,
    bool Installed, bool Authenticated, string? Version, string? Detail, int RunningNow, UsageLimitDto? UsageLimit);

/// <summary>Uma execução de agente em andamento (papel do fluxo) em qualquer projeto.</summary>
public record LiveRunDto(Guid RunId, string Role, Guid? ProjectId, string Project, Guid? WorkItemId, string? Task, Guid? EnvironmentId,
    string Environment, DateTime StartedAt);

public record LiveAgentsDto(List<LiveAgentDto> Agents, List<LiveRunDto> Running, DateTime At);

/// <summary>Painel "Agentes ao vivo": todos os agentes de todas as máquinas e o que está rodando agora.</summary>
public static class LiveEndpoints
{
    private static readonly TimeSpan PerEnvironmentTimeout = TimeSpan.FromSeconds(12);

    public static void MapLiveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/live/agents", (AppDbContext db, AgentCliService cli, IUsageLimitTracker tracker, AgentAdapterRegistry adapters, CancellationToken ct) =>
                SnapshotAsync(db, cli, tracker, adapters, ct))
            .RequireAuthorization().WithTags("Live")
            .WithSummary("Agentes (CLIs) de todos os ambientes, com status, uso e limite, e as execuções rodando agora.");
    }

    internal static async Task<LiveAgentsDto> SnapshotAsync(AppDbContext db, AgentCliService cli, IUsageLimitTracker tracker,
        AgentAdapterRegistry adapters, CancellationToken ct)
    {
        var environments = await db.ExecutionEnvironments.AsNoTracking().OrderBy(e => e.Name).Select(e => new { e.Id, e.Name }).ToListAsync(ct);
        var names = environments.ToDictionary(e => e.Id, e => e.Name);
        var running = await db.AgentRuns.AsNoTracking().Where(r => r.Status == AgentRunStatus.Running).OrderBy(r => r.StartedAt)
            .Select(r => new { r.Id, r.Role, r.DemandId, r.WorkItemId, r.EnvironmentId, r.StartedAt }).ToListAsync(ct);

        var itemIds = running.Where(r => r.WorkItemId != null).Select(r => r.WorkItemId!.Value).ToList();
        var demandIds = running.Where(r => r.DemandId != null).Select(r => r.DemandId!.Value).ToList();
        var items = await db.WorkItems.AsNoTracking().Where(w => itemIds.Contains(w.Id))
            .Join(db.Demands, w => w.DemandId, d => d.Id, (w, d) => new { w.Id, w.Title, d.ProjectId }).ToListAsync(ct);
        var demands = await db.Demands.AsNoTracking().Where(d => demandIds.Contains(d.Id)).Select(d => new { d.Id, d.Title, d.ProjectId }).ToListAsync(ct);
        var projectNames = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);

        var runs = running.Select(r =>
        {
            var item = r.WorkItemId is { } w ? items.FirstOrDefault(i => i.Id == w) : null;
            var demand = r.DemandId is { } d ? demands.FirstOrDefault(x => x.Id == d) : null;
            var projectId = item?.ProjectId ?? demand?.ProjectId;
            return new LiveRunDto(r.Id, r.Role.ToString(), projectId, projectId is { } p ? projectNames.GetValueOrDefault(p, "") : "",
                r.WorkItemId, item?.Title ?? demand?.Title, r.EnvironmentId,
                r.EnvironmentId is { } e ? names.GetValueOrDefault(e, "(ambiente removido)") : "Padrão do servidor", r.StartedAt);
        }).ToList();

        var limits = (await EnvironmentEndpoints.UsageLimitsAsync(db, tracker, adapters, ct));
        var targets = new List<(Guid? Id, string Name)> { (null, "Padrão do servidor") };
        targets.AddRange(environments.Select(e => ((Guid?)e.Id, e.Name)));

        var perEnvironment = await Task.WhenAll(targets.Select(t => AgentsOfAsync(cli, adapters, t.Id, t.Name, ct)));
        var agents = perEnvironment.SelectMany(x => x).Select(a => a with
        {
            RunningNow = running.Count(r => r.EnvironmentId == a.EnvironmentId && a.Agent == ClaudeCodeAdapter.AdapterName),
            UsageLimit = limits.FirstOrDefault(l => l.EnvironmentId == a.EnvironmentId && l.Agent == a.DisplayName),
        }).ToList();

        return new LiveAgentsDto(agents, runs, DateTime.UtcNow);
    }

    private static bool IsUnreachable(AgentInfo info) =>
        !info.Installed && info.Detail is { } d && (d.Contains("inacessível", StringComparison.Ordinal) || d.Contains("recusou o token", StringComparison.Ordinal));

    /// <summary>Os agentes de uma máquina; máquina fora do ar ou lenta não derruba o painel: aparece como inacessível.</summary>
    private static async Task<List<LiveAgentDto>> AgentsOfAsync(AgentCliService cli, AgentAdapterRegistry adapters, Guid? environmentId, string name, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerEnvironmentTimeout);
        try
        {
            var (mode, _) = await cli.EnvironmentAsync(environmentId, timeout.Token);
            var infos = await cli.ListAsync(environmentId, timeout.Token);
            // máquina fora do ar não lança: o serviço devolve "não instalado" com o motivo do launcher no detalhe
            return infos.Select(i => new LiveAgentDto(environmentId, name, mode, i.Name, i.DisplayName, !IsUnreachable(i), i.Installed, i.Authenticated,
                i.Version, i.Detail, 0, null)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var why = ex is OperationCanceledException ? "A máquina não respondeu a tempo." : ex.Message;
            return adapters.All.Select(a => new LiveAgentDto(environmentId, name, "host", a.Name,
                (a as ICliAgentAccount)?.DisplayName ?? a.Name, false, false, false, null, why, 0, null)).ToList();
        }
    }
}
