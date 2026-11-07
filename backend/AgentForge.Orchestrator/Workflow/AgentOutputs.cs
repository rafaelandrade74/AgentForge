using System.Text.Json;
using AgentForge.Domain;

namespace AgentForge.Orchestrator.Workflow;

public record Parsed<T>(T? Value, string? Error)
{
    public bool Ok => Error is null;
    public static Parsed<T> Success(T value) => new(value, null);
    public static Parsed<T> Fail(string error) => new(default, error);
}

public record PlannedTask(string Key, string Title, string Description, string AcceptanceCriteria, int Estimate, List<string> DependsOn);
public record DevReport(string Summary, List<string> Files, string Commit);
public record FindingData(string? File, int? Line, FindingSeverity Severity, string Message);
public record ReviewReport(bool Approved, List<FindingData> Findings);
public record QaCriterion(string Criterion, bool Passed, string Evidence);
public record QaReport(bool Passed, List<QaCriterion> Criteria);

public record ReassessAction(Guid WorkItemId, string Action, string? NewDescription, string? NewAcceptanceCriteria);
public record ReassessmentReport(List<ReassessAction> Actions);

/// <summary>Valida o bloco JSON final de cada papel. Mensagens de erro vão de volta ao agente na segunda tentativa.</summary>
public static class OutputParsers
{
    public const int MaxTasks = 30;

    public static Parsed<List<PlannedTask>> ParsePlan(string? json) => Guard(json, root =>
    {
        if (!root.TryGetProperty("tasks", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return Parsed<List<PlannedTask>>.Fail("\"tasks\" deve ser uma lista não vazia.");
        if (arr.GetArrayLength() > MaxTasks)
            return Parsed<List<PlannedTask>>.Fail($"No máximo {MaxTasks} tarefas.");

        var tasks = new List<PlannedTask>();
        foreach (var t in arr.EnumerateArray())
        {
            var key = Str(t, "key");
            var title = Str(t, "title");
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title))
                return Parsed<List<PlannedTask>>.Fail("Toda tarefa precisa de \"key\" e \"title\".");
            var deps = t.TryGetProperty("dependsOn", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : [];
            var estimate = t.TryGetProperty("estimate", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : 0;
            tasks.Add(new PlannedTask(key!, title!, Str(t, "description") ?? "", Criteria(t), estimate, deps));
        }

        var keys = tasks.Select(x => x.Key).ToList();
        if (keys.Distinct().Count() != keys.Count) return Parsed<List<PlannedTask>>.Fail("As chaves (\"key\") das tarefas devem ser únicas.");
        foreach (var t in tasks)
        {
            var unknown = t.DependsOn.FirstOrDefault(k => !keys.Contains(k));
            if (unknown is not null) return Parsed<List<PlannedTask>>.Fail($"A tarefa \"{t.Key}\" depende de \"{unknown}\", que não existe.");
            if (t.DependsOn.Contains(t.Key)) return Parsed<List<PlannedTask>>.Fail($"A tarefa \"{t.Key}\" depende dela mesma.");
        }
        return HasCycle(tasks) ? Parsed<List<PlannedTask>>.Fail("As dependências entre tarefas formam um ciclo.") : Parsed<List<PlannedTask>>.Success(tasks);
    });

    public static Parsed<DevReport> ParseDev(string? json) => Guard(json, root =>
    {
        var summary = Str(root, "summary");
        var commit = Str(root, "commit");
        if (string.IsNullOrWhiteSpace(summary)) return Parsed<DevReport>.Fail("\"summary\" é obrigatório.");
        if (string.IsNullOrWhiteSpace(commit)) return Parsed<DevReport>.Fail("\"commit\" (hash do commit feito) é obrigatório.");
        var files = root.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
        return Parsed<DevReport>.Success(new DevReport(summary!, files, commit!.Trim()));
    });

    public static Parsed<ReviewReport> ParseReview(string? json) => Guard(json, root =>
    {
        var verdict = Str(root, "verdict")?.Trim().ToLowerInvariant();
        if (verdict is not ("approved" or "changes_requested"))
            return Parsed<ReviewReport>.Fail("\"verdict\" deve ser \"approved\" ou \"changes_requested\".");

        var findings = new List<FindingData>();
        if (root.TryGetProperty("findings", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in arr.EnumerateArray())
            {
                var message = Str(f, "message");
                if (string.IsNullOrWhiteSpace(message)) return Parsed<ReviewReport>.Fail("Todo achado precisa de \"message\".");
                var severity = Enum.TryParse<FindingSeverity>(Str(f, "severity"), ignoreCase: true, out var s) ? s : FindingSeverity.Medium;
                var line = f.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var n) ? n : (int?)null;
                findings.Add(new FindingData(Str(f, "file"), line, severity, message!));
            }
        }
        return verdict == "changes_requested" && findings.Count == 0
            ? Parsed<ReviewReport>.Fail("\"changes_requested\" exige ao menos um item em \"findings\".")
            : Parsed<ReviewReport>.Success(new ReviewReport(verdict == "approved", findings));
    });

    public static Parsed<QaReport> ParseQa(string? json) => Guard(json, root =>
    {
        var verdict = Str(root, "verdict")?.Trim().ToLowerInvariant();
        if (verdict is not ("passed" or "failed"))
            return Parsed<QaReport>.Fail("\"verdict\" deve ser \"passed\" ou \"failed\".");

        var criteria = new List<QaCriterion>();
        if (root.TryGetProperty("criteria", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray())
                criteria.Add(new QaCriterion(Str(c, "criterion") ?? "", c.TryGetProperty("passed", out var p) && p.ValueKind == JsonValueKind.True, Str(c, "evidence") ?? ""));

        if (verdict == "passed" && criteria.Any(c => !c.Passed))
            return Parsed<QaReport>.Fail("\"passed\" não pode ter critério com \"passed\": false.");
        if (verdict == "failed" && criteria.All(c => c.Passed))
            return Parsed<QaReport>.Fail("\"failed\" exige ao menos um critério reprovado em \"criteria\".");
        return Parsed<QaReport>.Success(new QaReport(verdict == "passed", criteria));
    });

    private static Parsed<T> Guard<T>(string? json, Func<JsonElement, Parsed<T>> parse)
    {
        if (string.IsNullOrWhiteSpace(json)) return Parsed<T>.Fail("A resposta não terminou com um bloco JSON válido.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? parse(doc.RootElement)
                : Parsed<T>.Fail("O JSON final deve ser um objeto.");
        }
        catch (JsonException ex)
        {
            return Parsed<T>.Fail($"JSON inválido: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return Parsed<T>.Fail($"Campo com tipo inesperado: {ex.Message}");
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Criteria(JsonElement task)
    {
        if (!task.TryGetProperty("acceptanceCriteria", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return "";
        return string.Join("\n", c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => "- " + x.GetString()));
    }

    public static Parsed<ReassessmentReport> ParseReassessment(string? json, IReadOnlySet<Guid> validIds) => Guard(json, root =>
    {
        if (!root.TryGetProperty("actions", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return Parsed<ReassessmentReport>.Fail("\"actions\" deve ser uma lista não vazia.");

        var actions = new List<ReassessAction>();
        foreach (var a in arr.EnumerateArray())
        {
            var idStr = Str(a, "workItemId");
            if (!Guid.TryParse(idStr, out var id) || !validIds.Contains(id))
                return Parsed<ReassessmentReport>.Fail($"\"workItemId\" inválido ou fora do escopo: {idStr}");
            var action = Str(a, "action")?.Trim().ToLowerInvariant();
            if (action is not ("remove_dependency" or "block"))
                return Parsed<ReassessmentReport>.Fail("\"action\" deve ser \"remove_dependency\" ou \"block\".");
            actions.Add(new ReassessAction(id, action, Str(a, "newDescription"), Str(a, "newAcceptanceCriteria")));
        }
        return Parsed<ReassessmentReport>.Success(new ReassessmentReport(actions));
    });

    private static bool HasCycle(List<PlannedTask> tasks)
    {
        var remaining = tasks.ToDictionary(t => t.Key, t => t.DependsOn.ToHashSet());
        while (remaining.Count > 0)
        {
            var free = remaining.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
            if (free.Count == 0) return true;
            foreach (var k in free) remaining.Remove(k);
            foreach (var deps in remaining.Values) deps.ExceptWith(free);
        }
        return false;
    }
}
