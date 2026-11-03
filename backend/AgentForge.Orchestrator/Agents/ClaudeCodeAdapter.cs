using System.Text.Json;

namespace AgentForge.Orchestrator.Agents;

/// <summary>
/// Claude Code em modo headless: `claude -p --output-format stream-json --verbose`, com o prompt no stdin.
/// Flags conferidos com `claude --help` (v2.1.316); `--max-turns` segue aceito, embora fora da ajuda.
/// </summary>
public class ClaudeCodeAdapter : ICliAgentAdapter, ICliAgentAccount
{
    public const string AdapterName = "claude-code";
    private const int MaxEventText = 500;

    public string Name => AdapterName;
    public string Executable { get; } = Environment.GetEnvironmentVariable("CLAUDE_BIN") is { Length: > 0 } bin ? bin : "claude";

    // ---- conta (status, login por link, teste) — `claude auth ...`

    public string DisplayName => "Claude Code";
    public IReadOnlyList<string> VersionArguments => ["--version"];
    public IReadOnlyList<string> StatusArguments => ["auth", "status", "--json"];
    public IReadOnlyList<string>? LoginArguments => ["auth", "login"];
    public IReadOnlyList<string>? LogoutArguments => ["auth", "logout"];
    public string TestPrompt => "Responda apenas com a palavra OK.";

    public AgentAuthState ParseStatus(string stdout, int exitCode)
    {
        if (!TryParse(stdout.Trim(), out var doc)) return new AgentAuthState(false, null, "Resposta inesperada de `claude auth status`.");
        using (doc)
        {
            var root = doc.RootElement;
            var loggedIn = root.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
            var method = Str(root, "authMethod");
            if (!loggedIn) return new AgentAuthState(false, method, "Não conectado.");

            var who = new[] { Str(root, "email"), Str(root, "orgName"), Str(root, "subscriptionType") }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return new AgentAuthState(true, method, string.Join(" · ", who));
        }
    }

    public string? ExtractLoginUrl(string line)
    {
        var start = line.IndexOf("https://", StringComparison.Ordinal);
        if (start < 0) return null;
        var end = line.IndexOfAny([' ', '\t', '\r'], start);
        return end < 0 ? line[start..] : line[start..end];
    }

    public IReadOnlyList<string> BuildArguments(AgentRequest request)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt)) args.AddRange(["--append-system-prompt", request.SystemPrompt]);
        if (!string.IsNullOrWhiteSpace(request.AllowedTools)) args.AddRange(["--allowedTools", request.AllowedTools]);
        if (request.MaxTurns > 0) args.AddRange(["--max-turns", request.MaxTurns.ToString()]);
        if (!string.IsNullOrWhiteSpace(request.Model)) args.AddRange(["--model", request.Model]);
        return args;
    }

    public AgentEventData? ParseEvent(string line)
    {
        if (!TryParse(line, out var doc)) return string.IsNullOrWhiteSpace(line) ? null : new("raw", Cut(line));
        using (doc)
        {
            var root = doc.RootElement;
            switch (Str(root, "type"))
            {
                case "system" when Str(root, "subtype") == "init":
                    return new("system", $"Sessão iniciada (modelo {Str(root, "model") ?? "?"})");
                case "assistant":
                    return FromContent(root, "assistant");
                case "user":
                    return FromContent(root, "tool_result");
                case "result":
                    return new("result", Cut(Str(root, "result") ?? ""));
                default:
                    return null;
            }
        }
    }

    public AgentResult ParseResult(IReadOnlyList<string> stdoutLines, int exitCode)
    {
        for (var i = stdoutLines.Count - 1; i >= 0; i--)
        {
            if (!TryParse(stdoutLines[i], out var doc)) continue;
            using (doc)
            {
                var root = doc.RootElement;
                if (Str(root, "type") != "result") continue;

                var text = Str(root, "result") ?? "";
                var cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : 0m;
                var isError = exitCode != 0 || (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True);
                return new AgentResult(text, JsonBlock.ExtractLast(text), cost, isError);
            }
        }

        return new AgentResult("O CLI terminou sem emitir o evento de resultado.", null, 0m, true);
    }

    private static AgentEventData? FromContent(JsonElement root, string defaultKind)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array) return null;

        foreach (var block in content.EnumerateArray())
        {
            switch (Str(block, "type"))
            {
                case "text" when defaultKind == "assistant":
                    return new("assistant", Cut(Str(block, "text") ?? ""));
                case "tool_use":
                    var input = block.TryGetProperty("input", out var i) ? i.GetRawText() : "";
                    return new("tool", Cut($"{Str(block, "name")}: {input}"));
                case "tool_result":
                    return new("tool_result", Cut(block.TryGetProperty("content", out var r) ? r.ToString() : ""));
            }
        }
        return null;
    }

    private static bool TryParse(string line, out JsonDocument doc)
    {
        doc = null!;
        if (!line.StartsWith('{')) return false;
        try
        {
            doc = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cut(string s) => s.Length <= MaxEventText ? s : s[..MaxEventText] + "…";
}
