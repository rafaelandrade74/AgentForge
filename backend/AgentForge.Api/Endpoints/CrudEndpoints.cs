using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Agents;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Api.Endpoints;

public static class CrudEndpoints
{
    public static void MapCrudEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        MapProjects(api.MapGroup("/projects").WithTags("Projects"));
        MapDemands(api.MapGroup("/demands").WithTags("Demands"));
        MapWorkItems(api.MapGroup("/work-items").WithTags("WorkItems"));
        MapAgentRuns(api.MapGroup("/agent-runs").WithTags("AgentRuns"));
        MapFindings(api.MapGroup("/review-findings").WithTags("ReviewFindings"));
        MapRoleConfigs(api.MapGroup("/role-configs").WithTags("RoleConfigs"));
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    // ---------- Projects ----------
    internal static ProjectDto ToDto(Project p) =>
        new(p.Id, p.Name, Secrets.MaskUrls(p.RepositoryUrl), p.BaseBranch, p.BuildCommand, p.TestCommand, p.AutoMode, p.CreatedAt);

    private static void Apply(Project p, ProjectInput i)
    {
        p.Name = i.Name.Trim();
        // a API devolve https://***@host; reenviar a URL mascarada (ex.: editar só o nome) mantém a credencial guardada
        var url = i.RepositoryUrl.Trim();
        if (!(Secrets.IsMasked(url) && url == Secrets.MaskUrls(p.RepositoryUrl))) p.RepositoryUrl = url;
        p.BaseBranch = string.IsNullOrWhiteSpace(i.BaseBranch) ? "main" : i.BaseBranch.Trim();
        p.BuildCommand = i.BuildCommand;
        p.TestCommand = i.TestCommand;
        p.AutoMode = i.AutoMode;
    }

    private static IResult? Validate(ProjectInput i) =>
        string.IsNullOrWhiteSpace(i.Name) ? Invalid(nameof(i.Name), "Informe o nome.")
        : string.IsNullOrWhiteSpace(i.RepositoryUrl) ? Invalid(nameof(i.RepositoryUrl), "Informe a URL do repositório.")
        : null;

    /// <summary>URL mascarada que não corresponde à guardada (ou projeto novo): não há credencial para reaproveitar.</summary>
    private static IResult? ValidateMasked(ProjectInput i, Project? existing) =>
        Secrets.IsMasked(i.RepositoryUrl) && (existing is null || i.RepositoryUrl.Trim() != Secrets.MaskUrls(existing.RepositoryUrl))
            ? Invalid(nameof(i.RepositoryUrl), "A URL contém credencial mascarada (***). Informe a URL completa.")
            : null;

    private static void MapProjects(RouteGroupBuilder g)
    {
        g.MapGet("/", async (AppDbContext db) =>
            (await db.Projects.OrderBy(p => p.Name).ToListAsync()).Select(ToDto));
        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Projects.FindAsync(id) is { } p ? Results.Ok(ToDto(p)) : Results.NotFound());
        g.MapPost("/", async (ProjectInput i, AppDbContext db) =>
        {
            if (Validate(i) is { } bad) return bad;
            if (ValidateMasked(i, null) is { } masked) return masked;
            var p = new Project();
            Apply(p, i);
            db.Projects.Add(p);
            await db.SaveChangesAsync();
            return Results.Created($"/api/projects/{p.Id}", ToDto(p));
        });
        g.MapPut("/{id:guid}", async (Guid id, ProjectInput i, AppDbContext db) =>
        {
            if (Validate(i) is { } bad) return bad;
            if (await db.Projects.FindAsync(id) is not { } p) return Results.NotFound();
            if (ValidateMasked(i, p) is { } masked) return masked;
            Apply(p, i);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(p));
        });
        g.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());
    }

    // ---------- Demands ----------
    internal static DemandDto ToDto(Demand d) => new(d.Id, d.ProjectId, d.Title, d.Description, d.Status, d.CreatedAt);

    private static async Task<IResult?> ValidateAsync(DemandInput i, AppDbContext db) =>
        string.IsNullOrWhiteSpace(i.Title) ? Invalid(nameof(i.Title), "Informe o título.")
        : !await db.Projects.AnyAsync(p => p.Id == i.ProjectId) ? Invalid(nameof(i.ProjectId), "Projeto não encontrado.")
        : null;

    private static void MapDemands(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid? projectId, AppDbContext db) =>
            (await db.Demands.Where(d => projectId == null || d.ProjectId == projectId)
                .OrderByDescending(d => d.CreatedAt).ToListAsync()).Select(ToDto));
        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Demands.FindAsync(id) is { } d ? Results.Ok(ToDto(d)) : Results.NotFound());
        g.MapPost("/", async (DemandInput i, AppDbContext db) =>
        {
            if (await ValidateAsync(i, db) is { } bad) return bad;
            var d = new Demand { ProjectId = i.ProjectId, Title = i.Title.Trim(), Description = i.Description, Status = i.Status };
            db.Demands.Add(d);
            await db.SaveChangesAsync();
            return Results.Created($"/api/demands/{d.Id}", ToDto(d));
        });
        g.MapPut("/{id:guid}", async (Guid id, DemandInput i, AppDbContext db) =>
        {
            if (await ValidateAsync(i, db) is { } bad) return bad;
            if (await db.Demands.FindAsync(id) is not { } d) return Results.NotFound();
            d.ProjectId = i.ProjectId;
            d.Title = i.Title.Trim();
            d.Description = i.Description;
            d.Status = i.Status;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(d));
        });
        g.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Demands.Where(d => d.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());
    }

    // ---------- WorkItems ----------
    internal static WorkItemDto ToDto(WorkItem w) =>
        new(w.Id, w.DemandId, w.Title, w.Description, w.AcceptanceCriteria, w.Estimate, w.State, w.ReworkCount,
            w.BranchName, w.BlockedReason, w.Dependencies.Select(d => d.Id).ToList(), w.CreatedAt);

    private static async Task<(IResult? Error, List<WorkItem> Deps)> ResolveAsync(WorkItemInput i, Guid? selfId, AppDbContext db)
    {
        if (string.IsNullOrWhiteSpace(i.Title)) return (Invalid(nameof(i.Title), "Informe o título."), []);
        if (!await db.Demands.AnyAsync(d => d.Id == i.DemandId)) return (Invalid(nameof(i.DemandId), "Demanda não encontrada."), []);
        var ids = (i.DependsOnIds ?? []).Distinct().ToList();
        if (selfId is { } self && ids.Contains(self)) return (Invalid(nameof(i.DependsOnIds), "Uma tarefa não pode depender dela mesma."), []);
        var deps = await db.WorkItems.Where(w => ids.Contains(w.Id)).ToListAsync();
        return deps.Count != ids.Count ? (Invalid(nameof(i.DependsOnIds), "Dependência não encontrada."), []) : (null, deps);
    }

    private static void MapWorkItems(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid? demandId, Guid? projectId, WorkItemState? state, AppDbContext db) =>
            (await db.WorkItems.Include(w => w.Dependencies)
                .Where(w => (demandId == null || w.DemandId == demandId) && (state == null || w.State == state)
                            && (projectId == null || db.Demands.Any(d => d.Id == w.DemandId && d.ProjectId == projectId)))
                .OrderBy(w => w.CreatedAt).ToListAsync()).Select(ToDto));
        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.WorkItems.Include(w => w.Dependencies).FirstOrDefaultAsync(w => w.Id == id) is { } w
                ? Results.Ok(ToDto(w)) : Results.NotFound());
        g.MapPost("/", async (WorkItemInput i, AppDbContext db) =>
        {
            var (error, deps) = await ResolveAsync(i, null, db);
            if (error is not null) return error;
            var w = new WorkItem
            {
                DemandId = i.DemandId, Title = i.Title.Trim(), Description = i.Description,
                AcceptanceCriteria = i.AcceptanceCriteria, Estimate = i.Estimate, State = i.State, Dependencies = deps,
            };
            db.WorkItems.Add(w);
            await db.SaveChangesAsync();
            return Results.Created($"/api/work-items/{w.Id}", ToDto(w));
        });
        g.MapPut("/{id:guid}", async (Guid id, WorkItemInput i, AppDbContext db) =>
        {
            var w = await db.WorkItems.Include(x => x.Dependencies).FirstOrDefaultAsync(x => x.Id == id);
            if (w is null) return Results.NotFound();
            var (error, deps) = await ResolveAsync(i, id, db);
            if (error is not null) return error;
            w.DemandId = i.DemandId;
            w.Title = i.Title.Trim();
            w.Description = i.Description;
            w.AcceptanceCriteria = i.AcceptanceCriteria;
            w.Estimate = i.Estimate;
            w.State = i.State;
            w.Dependencies.Clear();
            w.Dependencies.AddRange(deps);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(w));
        });
        g.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            // Dependências de outras tarefas apontando para esta usam Restrict; remove os vínculos antes.
            await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "WorkItemDependencies" WHERE "DependsOnId" = {id}""");
            return await db.WorkItems.Where(w => w.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound();
        });
    }

    // ---------- AgentRuns (criadas pelo orquestrador; a API só consulta) ----------
    internal static AgentRunDto ToDto(AgentRun r) =>
        new(r.Id, r.DemandId, r.WorkItemId, r.Role, r.Prompt, r.Output, r.CostUsd, r.DurationMs, r.Status, r.StartedAt, r.FinishedAt);

    private static void MapAgentRuns(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid? workItemId, Guid? demandId, AppDbContext db) =>
            (await db.AgentRuns
                .Where(r => (workItemId == null || r.WorkItemId == workItemId) && (demandId == null || r.DemandId == demandId))
                .OrderBy(r => r.StartedAt)
                .Select(r => new { r.Id, r.DemandId, r.WorkItemId, r.Role, r.CostUsd, r.DurationMs, r.Status, r.StartedAt, r.FinishedAt })
                .ToListAsync())
            .Select(r => new AgentRunSummaryDto(r.Id, r.DemandId, r.WorkItemId, r.Role, r.CostUsd, r.DurationMs, r.Status, r.StartedAt, r.FinishedAt)));
        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.AgentRuns.FindAsync(id) is { } r ? Results.Ok(ToDto(r)) : Results.NotFound());
    }

    // ---------- ReviewFindings ----------
    internal static ReviewFindingDto ToDto(ReviewFinding f) =>
        new(f.Id, f.WorkItemId, f.AgentRunId, f.Role, f.Severity, f.File, f.Line, f.Message, f.Resolved);

    private static async Task<IResult?> ValidateAsync(ReviewFindingInput i, AppDbContext db) =>
        string.IsNullOrWhiteSpace(i.Message) ? Invalid(nameof(i.Message), "Informe a mensagem.")
        : !await db.WorkItems.AnyAsync(w => w.Id == i.WorkItemId) ? Invalid(nameof(i.WorkItemId), "Tarefa não encontrada.")
        : i.AgentRunId is { } run && !await db.AgentRuns.AnyAsync(r => r.Id == run) ? Invalid(nameof(i.AgentRunId), "Execução não encontrada.")
        : null;

    private static void Apply(ReviewFinding f, ReviewFindingInput i)
    {
        f.WorkItemId = i.WorkItemId; f.AgentRunId = i.AgentRunId; f.Role = i.Role; f.Severity = i.Severity;
        f.File = i.File; f.Line = i.Line; f.Message = i.Message; f.Resolved = i.Resolved;
    }

    private static void MapFindings(RouteGroupBuilder g)
    {
        g.MapGet("/", async (Guid? workItemId, AppDbContext db) =>
            (await db.ReviewFindings.Where(f => workItemId == null || f.WorkItemId == workItemId).ToListAsync()).Select(ToDto));
        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.ReviewFindings.FindAsync(id) is { } f ? Results.Ok(ToDto(f)) : Results.NotFound());
        g.MapPost("/", async (ReviewFindingInput i, AppDbContext db) =>
        {
            if (await ValidateAsync(i, db) is { } bad) return bad;
            var f = new ReviewFinding();
            Apply(f, i);
            db.ReviewFindings.Add(f);
            await db.SaveChangesAsync();
            return Results.Created($"/api/review-findings/{f.Id}", ToDto(f));
        });
        g.MapPut("/{id:guid}", async (Guid id, ReviewFindingInput i, AppDbContext db) =>
        {
            if (await ValidateAsync(i, db) is { } bad) return bad;
            if (await db.ReviewFindings.FindAsync(id) is not { } f) return Results.NotFound();
            Apply(f, i);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(f));
        });
        g.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.ReviewFindings.Where(f => f.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());
    }

    // ---------- AgentRoleConfigs ----------
    internal static RoleConfigDto ToDto(AgentRoleConfig c) =>
        new(c.Id, c.Role, c.AdapterName, c.SystemPrompt, c.AllowedTools, c.Model, c.MaxTurns);

    private static IResult? Validate(RoleConfigInput i, AgentAdapterRegistry registry) =>
        string.IsNullOrWhiteSpace(i.AdapterName) ? Invalid(nameof(i.AdapterName), "Informe o adapter.")
        : registry.TryGet(i.AdapterName.Trim()) is null ? Invalid(nameof(i.AdapterName), $"Agente \"{i.AdapterName}\" não existe.")
        : i.MaxTurns < 1 ? Invalid(nameof(i.MaxTurns), "MaxTurns deve ser ao menos 1.")
        : null;

    private static void Apply(AgentRoleConfig c, RoleConfigInput i)
    {
        c.Role = i.Role; c.AdapterName = i.AdapterName.Trim(); c.SystemPrompt = i.SystemPrompt;
        c.AllowedTools = i.AllowedTools; c.Model = i.Model; c.MaxTurns = i.MaxTurns;
    }

    private static void MapRoleConfigs(RouteGroupBuilder g)
    {
        g.MapGet("/", async (AppDbContext db) => (await db.AgentRoleConfigs.OrderBy(c => c.Role).ToListAsync()).Select(ToDto));
        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.AgentRoleConfigs.FindAsync(id) is { } c ? Results.Ok(ToDto(c)) : Results.NotFound());
        g.MapPost("/", async (RoleConfigInput i, AppDbContext db, AgentAdapterRegistry registry) =>
        {
            if (Validate(i, registry) is { } bad) return bad;
            if (await db.AgentRoleConfigs.AnyAsync(c => c.Role == i.Role)) return Results.Conflict(new { error = "Já existe configuração para este papel." });
            var c = new AgentRoleConfig();
            Apply(c, i);
            db.AgentRoleConfigs.Add(c);
            await db.SaveChangesAsync();
            return Results.Created($"/api/role-configs/{c.Id}", ToDto(c));
        });
        g.MapPut("/{id:guid}", async (Guid id, RoleConfigInput i, AppDbContext db, AgentAdapterRegistry registry) =>
        {
            if (Validate(i, registry) is { } bad) return bad;
            if (await db.AgentRoleConfigs.FindAsync(id) is not { } c) return Results.NotFound();
            if (await db.AgentRoleConfigs.AnyAsync(x => x.Role == i.Role && x.Id != id)) return Results.Conflict(new { error = "Já existe configuração para este papel." });
            Apply(c, i);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(c));
        });
        g.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.AgentRoleConfigs.Where(c => c.Id == id).ExecuteDeleteAsync() > 0 ? Results.NoContent() : Results.NotFound());
    }
}
