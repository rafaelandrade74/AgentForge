using System.Text.Json;
using System.Text.Json.Nodes;
using AgentForge.Api.Endpoints;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Workflow;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Mcp;

public sealed class ToolContext(JsonObject args, IServiceProvider services, CancellationToken ct)
{
    public IServiceProvider Services { get; } = services;
    public CancellationToken Ct { get; } = ct;
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();
    public AppDbContext Db => Get<AppDbContext>();

    public string Text(string name, bool required = true, int max = 20_000)
    {
        var value = args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrWhiteSpace(value))
            return required ? throw new ToolInputException($"O argumento '{name}' é obrigatório.") : "";
        if (value.Length > max) throw new ToolInputException($"O argumento '{name}' passa de {max} caracteres.");
        return value;
    }

    public Guid Id(string name) =>
        Guid.TryParse(Text(name), out var id) ? id : throw new ToolInputException($"'{name}' precisa ser um UUID válido.");

    public Guid? OptionalId(string name) =>
        string.IsNullOrWhiteSpace(args[name]?.ToString()) ? null
        : Guid.TryParse(args[name]!.ToString(), out var id) ? id : throw new ToolInputException($"'{name}' precisa ser um UUID válido.");

    public bool Bool(string name, bool fallback = false) =>
        args[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public int Int(string name, int fallback, int min, int max) =>
        args[name] is JsonValue v && v.TryGetValue<int>(out var n) ? Math.Clamp(n, min, max) : fallback;
}

public sealed record McpTool(
    string Name, string Title, string Description, JsonObject InputSchema,
    bool ReadOnly, bool Destructive, Func<ToolContext, Task<object>> Run);

/// <summary>As ferramentas que o AgentForge oferece por MCP. Nenhuma faz push, merge nem apaga projetos.</summary>
public sealed class McpToolRegistry
{
    private const int MaxDiffChars = 100_000;
    private readonly List<McpTool> _tools = [];

    public McpToolRegistry()
    {
        Register();
    }

    public McpTool? Find(string name) => _tools.FirstOrDefault(t => t.Name == name);

    public JsonArray Describe() => new(_tools.Select(t => (JsonNode)new JsonObject
    {
        ["name"] = t.Name,
        ["title"] = t.Title,
        ["description"] = t.Description,
        ["inputSchema"] = t.InputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = t.Title,
            ["readOnlyHint"] = t.ReadOnly,
            ["destructiveHint"] = t.Destructive,
            ["idempotentHint"] = t.ReadOnly,
            ["openWorldHint"] = false,
        },
    }).ToArray());

    // ---------------------------------------------------------------------------------------------- catálogo

    private void Register()
    {
        Add("list_projects", "Listar projetos", "Lista os projetos cadastrados.", Schema(), readOnly: true,
            async c => (await c.Db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(c.Ct)).Select(CrudEndpoints.ToDto));

        Add("get_project", "Ver projeto", "Mostra um projeto e as suas demandas.", Schema(Id("projectId", "UUID do projeto")), readOnly: true,
            async c =>
            {
                var id = c.Id("projectId");
                var project = await c.Db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, c.Ct) ?? throw NotFound("Projeto");
                var demands = await c.Db.Demands.AsNoTracking().Where(d => d.ProjectId == id).OrderByDescending(d => d.CreatedAt).ToListAsync(c.Ct);
                return new { project = CrudEndpoints.ToDto(project), demands = demands.Select(CrudEndpoints.ToDto) };
            });

        Add("create_project", "Criar projeto",
            "Cadastra um projeto (repositório Git). A URL é resolvida onde o git roda (container, ou o computador no modo host agent).",
            Schema(Text("name", "Nome do projeto", req: true), Text("repositoryUrl", "URL ou caminho do repositório Git", req: true),
                Text("baseBranch", "Branch base (padrão: main)"), Text("buildCommand", "Comando de build, ex.: dotnet build"),
                Text("testCommand", "Comando de teste, ex.: dotnet test"),
                Bool("autoMode", "true desliga as aprovações humanas (plano e entrega); padrão false")),
            readOnly: false, destructive: false,
            async c =>
            {
                var p = new Project
                {
                    Name = c.Text("name", max: 200).Trim(), RepositoryUrl = c.Text("repositoryUrl", max: 500).Trim(),
                    BaseBranch = c.Text("baseBranch", required: false, max: 200) is { Length: > 0 } b ? b.Trim() : "main",
                    BuildCommand = c.Text("buildCommand", required: false, max: 500), TestCommand = c.Text("testCommand", required: false, max: 500),
                    AutoMode = c.Bool("autoMode"),
                };
                if (p.RepositoryUrl.StartsWith('-')) throw new ToolInputException("URL de repositório inválida.");
                c.Db.Projects.Add(p);
                await c.Db.SaveChangesAsync(c.Ct);
                return CrudEndpoints.ToDto(p);
            });

        Add("create_demand", "Criar demanda",
            "Cria uma demanda em linguagem natural. Com start=true já inicia o refinamento: o Scrum Master lê o repositório e propõe as tarefas (gasta créditos do agente).",
            Schema(Id("projectId", "UUID do projeto", req: true), Text("title", "Título curto", req: true),
                Text("description", "O que precisa ser feito, com critérios claros"), Bool("start", "Iniciar o refinamento agora (padrão false)")),
            readOnly: false, destructive: false,
            async c =>
            {
                var projectId = c.Id("projectId");
                if (!await c.Db.Projects.AnyAsync(p => p.Id == projectId, c.Ct)) throw NotFound("Projeto");
                var d = new Demand { ProjectId = projectId, Title = c.Text("title", max: 300).Trim(), Description = c.Text("description", required: false) };
                c.Db.Demands.Add(d);
                await c.Db.SaveChangesAsync(c.Ct);
                if (c.Bool("start")) Outcome(await c.Get<WorkflowActions>().StartDemandAsync(d.Id), "Demanda");
                var saved = await c.Db.Demands.AsNoTracking().FirstAsync(x => x.Id == d.Id, c.Ct);
                return new { demand = CrudEndpoints.ToDto(saved), nextStep = c.Bool("start") ? "Aguarde o plano (get_demand) e aprove com approve_plan." : "Inicie com start_demand." };
            });

        Add("start_demand", "Iniciar refinamento", "Inicia (ou reinicia, se falhou) o refinamento da demanda pelo Scrum Master. Gasta créditos do agente.",
            Schema(Id("demandId", "UUID da demanda", req: true)), readOnly: false, destructive: false,
            async c => Outcome(await c.Get<WorkflowActions>().StartDemandAsync(c.Id("demandId")), "Demanda", "Refinamento iniciado."));

        Add("approve_plan", "Aprovar plano", "Aprovação humana do plano: libera todas as tarefas em Backlog da demanda para os agentes.",
            Schema(Id("demandId", "UUID da demanda", req: true)), readOnly: false, destructive: false,
            async c => Outcome(await c.Get<WorkflowActions>().ApprovePlanAsync(c.Id("demandId")), "Demanda", "Plano aprovado: as tarefas foram liberadas."));

        Add("get_demand", "Ver demanda", "Estado da demanda, das suas tarefas (com dependências), custo acumulado e o que depende de você.",
            Schema(Id("demandId", "UUID da demanda", req: true)), readOnly: true,
            async c =>
            {
                var id = c.Id("demandId");
                var demand = await c.Db.Demands.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, c.Ct) ?? throw NotFound("Demanda");
                var items = await c.Db.WorkItems.AsNoTracking().Include(w => w.Dependencies).Where(w => w.DemandId == id).OrderBy(w => w.CreatedAt).ToListAsync(c.Ct);
                var titles = items.ToDictionary(i => i.Id, i => i.Title);
                var cost = await c.Db.AgentRuns.Where(r => r.DemandId == id).SumAsync(r => (decimal?)r.CostUsd, c.Ct) ?? 0m;
                return new
                {
                    demand = CrudEndpoints.ToDto(demand),
                    tasks = items.Select(i => new
                    {
                        i.Id, i.Title, i.State, i.ReworkCount, i.BlockedReason, i.BranchName,
                        dependsOn = i.Dependencies.Select(d => titles.GetValueOrDefault(d.Id, d.Title)),
                        nextAction = NextAction(i.State),
                    }),
                    costUsd = cost,
                    nextAction = demand.Status switch
                    {
                        DemandStatus.New or DemandStatus.Failed => "start_demand",
                        _ when items.Any(i => i.State == WorkItemState.Backlog) => "approve_plan",
                        _ => null,
                    },
                };
            });

        Add("list_tasks", "Listar tarefas", "Lista tarefas por projeto ou demanda, opcionalmente filtrando por estado.",
            Schema(Id("projectId", "UUID do projeto"), Id("demandId", "UUID da demanda"),
                Text("state", "Backlog, Todo, InDevelopment, TechLeadReview, SecurityReview, Qa, AwaitingApproval, Done, Blocked ou Cancelled")),
            readOnly: true,
            async c =>
            {
                var projectId = c.OptionalId("projectId");
                var demandId = c.OptionalId("demandId");
                WorkItemState? state = null;
                if (c.Text("state", required: false) is { Length: > 0 } s)
                    state = Enum.TryParse<WorkItemState>(s, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                        ? parsed : throw new ToolInputException($"Estado '{s}' inválido.");
                var items = await c.Db.WorkItems.AsNoTracking().Include(w => w.Dependencies)
                    .Where(w => (demandId == null || w.DemandId == demandId) && (state == null || w.State == state)
                                && (projectId == null || c.Db.Demands.Any(d => d.Id == w.DemandId && d.ProjectId == projectId)))
                    .OrderBy(w => w.CreatedAt).Take(200).ToListAsync(c.Ct);
                return items.Select(CrudEndpoints.ToDto);
            });

        Add("get_task", "Ver tarefa",
            "Tudo para decidir sobre uma tarefa: descrição, critérios, estado, apontamentos das revisões, execuções (com custo) e a próxima ação possível.",
            Schema(Id("taskId", "UUID da tarefa", req: true)), readOnly: true,
            async c =>
            {
                var id = c.Id("taskId");
                var item = await c.Db.WorkItems.AsNoTracking().Include(w => w.Dependencies).FirstOrDefaultAsync(w => w.Id == id, c.Ct) ?? throw NotFound("Tarefa");
                var findings = await c.Db.ReviewFindings.AsNoTracking().Where(f => f.WorkItemId == id).ToListAsync(c.Ct);
                var runs = await c.Db.AgentRuns.AsNoTracking().Where(r => r.WorkItemId == id).OrderBy(r => r.StartedAt)
                    .Select(r => new { r.Id, r.Role, r.Status, r.CostUsd, r.DurationMs, r.StartedAt }).ToListAsync(c.Ct);
                return new
                {
                    task = CrudEndpoints.ToDto(item),
                    item.AcceptanceCriteria,
                    findings = findings.Select(CrudEndpoints.ToDto),
                    runs,
                    costUsd = runs.Sum(r => r.CostUsd),
                    nextAction = NextAction(item.State),
                };
            });

        Add("get_task_diff", "Ver diff da tarefa", "Diff do que a tarefa mudou no branch dela (sem o trabalho das dependências).",
            Schema(Id("taskId", "UUID da tarefa", req: true)), readOnly: true,
            async c =>
            {
                var id = c.Id("taskId");
                var item = await c.Db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, c.Ct) ?? throw NotFound("Tarefa");
                var project = await c.Db.Projects.AsNoTracking().FirstAsync(p => p.Id == c.Db.Demands.First(d => d.Id == item.DemandId).ProjectId, c.Ct);
                var diff = await c.Get<IWorkspaceManager>().GetDiffAsync(project, id, c.Ct, item.BaseCommit);
                if (diff.Length == 0) return "(sem mudanças no branch ainda)";
                return diff.Length <= MaxDiffChars ? diff : diff[..MaxDiffChars] + "\n… (diff truncado)";
            });

        Add("get_run_output", "Ver saída de uma execução", "Saída final de uma execução de agente (pode ser longa; é truncada).",
            Schema(Id("runId", "UUID da execução", req: true), Int("maxChars", "Limite de caracteres (padrão 8000, máximo 50000)")), readOnly: true,
            async c =>
            {
                var id = c.Id("runId");
                var run = await c.Db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, c.Ct) ?? throw NotFound("Execução");
                var max = c.Int("maxChars", 8000, 500, 50_000);
                return new
                {
                    run.Id, run.Role, run.Status, run.CostUsd, run.DurationMs,
                    output = run.Output.Length <= max ? run.Output : run.Output[..max] + "\n… (truncado)",
                };
            });

        Add("approve_task", "Aprovar tarefa", "Aprova a tarefa: libera o plano (Backlog) ou aceita a entrega (Aguardando aprovação → Done).",
            Schema(Id("taskId", "UUID da tarefa", req: true)), readOnly: false, destructive: false,
            async c => Outcome(await c.Get<WorkflowActions>().ApproveAsync(c.Id("taskId")), "Tarefa", "Aprovada."));

        Add("reject_task", "Rejeitar entrega", "Rejeita a entrega e devolve a tarefa ao Dev, com o comentário como apontamento.",
            Schema(Id("taskId", "UUID da tarefa", req: true), Text("comment", "O que precisa mudar", req: true)), readOnly: false, destructive: false,
            async c => Outcome(await c.Get<WorkflowActions>().RejectAsync(c.Id("taskId"), c.Text("comment", max: 4000)), "Tarefa", "Devolvida ao Dev."));

        Add("retry_task", "Reexecutar tarefa bloqueada", "Tira a tarefa de Blocked e a devolve ao Dev (zera o contador de retrabalho).",
            Schema(Id("taskId", "UUID da tarefa", req: true)), readOnly: false, destructive: false,
            async c => Outcome(await c.Get<WorkflowActions>().RetryAsync(c.Id("taskId")), "Tarefa", "Devolvida ao Dev."));

        Add("cancel_task", "Cancelar tarefa", "Cancela a tarefa: interrompe o agente em execução e apaga o worktree e o branch dela.",
            Schema(Id("taskId", "UUID da tarefa", req: true)), readOnly: false, destructive: true,
            async c => Outcome(await c.Get<WorkflowActions>().CancelAsync(c.Id("taskId")), "Tarefa", "Cancelada."));

        Add("list_pending_actions", "O que depende de mim", "Tudo que espera uma decisão humana: planos a aprovar, entregas a revisar, tarefas bloqueadas e demandas que falharam.",
            Schema(), readOnly: true,
            async c =>
            {
                var db = c.Db;
                var waiting = await db.WorkItems.AsNoTracking()
                    .Where(w => w.State == WorkItemState.Backlog || w.State == WorkItemState.AwaitingApproval || w.State == WorkItemState.Blocked)
                    .Join(db.Demands, w => w.DemandId, d => d.Id, (w, d) => new { w.Id, w.Title, w.State, w.BlockedReason, DemandId = d.Id, Demand = d.Title, d.ProjectId })
                    .ToListAsync(c.Ct);
                var failed = await db.Demands.AsNoTracking().Where(d => d.Status == DemandStatus.Failed || d.Status == DemandStatus.New)
                    .Select(d => new { d.Id, d.Title, d.Status, d.ProjectId }).ToListAsync(c.Ct);
                return new
                {
                    plansToApprove = waiting.Where(w => w.State == WorkItemState.Backlog).GroupBy(w => w.DemandId)
                        .Select(g => new { demandId = g.Key, demand = g.First().Demand, tasks = g.Count(), action = "approve_plan" }),
                    deliveriesToReview = waiting.Where(w => w.State == WorkItemState.AwaitingApproval)
                        .Select(w => new { taskId = w.Id, w.Title, w.Demand, action = "get_task, get_task_diff → approve_task ou reject_task" }),
                    blocked = waiting.Where(w => w.State == WorkItemState.Blocked)
                        .Select(w => new { taskId = w.Id, w.Title, w.Demand, reason = w.BlockedReason, action = "retry_task ou cancel_task" }),
                    demandsNotStarted = failed.Select(d => new { demandId = d.Id, d.Title, d.Status, action = "start_demand" }),
                };
            });

        Add("get_environment_load", "Carga dos ambientes", "Mostra, por ambiente (máquina), quantas tarefas rodam, quantas esperam vaga, quantos agentes estão rodando e o que aguarda uma máquina fora do ar voltar.",
            Schema(), readOnly: true,
            async c => await EnvironmentEndpoints.LoadAsync(c.Db, c.Get<IEnvironmentLoad>(), c.Get<IOptions<OrchestratorOptions>>().Value, c.Ct));

        Add("list_agents", "Agentes e conexão", "Mostra os agentes (CLIs) instalados, se estão conectados e quais papéis usam cada um. Não gasta créditos.",
            Schema(), readOnly: true, async c => await c.Get<AgentCliService>().ListAsync(c.Ct));

        Add("test_agent", "Testar agente", "Faz uma chamada real e mínima ao agente para testar a conexão. Gasta uma pequena quantia de créditos.",
            Schema(Text("name", "Nome do agente, ex.: claude-code", req: true)), readOnly: false, destructive: false,
            async c =>
            {
                try { return await c.Get<AgentCliService>().TestAsync(c.Text("name", max: 100), c.Ct); }
                catch (AgentNotFoundException ex) { throw new ToolInputException(ex.Message); }
            });
    }

    // ---------------------------------------------------------------------------------------------- helpers

    private void Add<T>(string name, string title, string description, JsonObject schema, bool readOnly,
        Func<ToolContext, Task<T>> run) where T : notnull =>
        Add(name, title, description, schema, readOnly, destructive: false, run);

    private void Add<T>(string name, string title, string description, JsonObject schema, bool readOnly, bool destructive,
        Func<ToolContext, Task<T>> run) where T : notnull =>
        _tools.Add(new McpTool(name, title, description, schema, readOnly, destructive, async c => (object)(await run(c))));
    private static ToolInputException NotFound(string what) => new($"{what} não encontrado(a).");

    private static string Outcome(ActionOutcome outcome, string what, string ok = "Feito.") => outcome switch
    {
        ActionOutcome.Ok => ok,
        ActionOutcome.NotFound => throw NotFound(what),
        _ => throw new ToolInputException($"A ação não vale para o estado atual ({what.ToLowerInvariant()}). Use get_task/get_demand para ver o estado e a próxima ação."),
    };

    private static string? NextAction(WorkItemState state) => state switch
    {
        WorkItemState.Backlog => "approve_task (ou approve_plan na demanda)",
        WorkItemState.AwaitingApproval => "get_task_diff, depois approve_task ou reject_task",
        WorkItemState.Blocked => "retry_task ou cancel_task",
        WorkItemState.Done or WorkItemState.Cancelled => null,
        _ => "aguardando os agentes",
    };

    private static JsonObject Schema(params (string Name, JsonObject Def, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, def, req) in props)
        {
            properties[name] = def;
            if (req) required.Add(name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static (string, JsonObject, bool) Text(string name, string description, bool req = false) =>
        (name, new JsonObject { ["type"] = "string", ["description"] = description }, req);

    private static (string, JsonObject, bool) Id(string name, string description, bool req = false) =>
        (name, new JsonObject { ["type"] = "string", ["format"] = "uuid", ["description"] = description }, req);

    private static (string, JsonObject, bool) Bool(string name, string description) =>
        (name, new JsonObject { ["type"] = "boolean", ["description"] = description }, false);

    private static (string, JsonObject, bool) Int(string name, string description) =>
        (name, new JsonObject { ["type"] = "integer", ["description"] = description }, false);
}
