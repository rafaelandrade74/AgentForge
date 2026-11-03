using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentForge.Orchestrator.Agents;

/// <summary>Extrai o bloco JSON que termina a resposta de um agente.</summary>
public static partial class JsonBlock
{
    [GeneratedRegex(@"```(?:json)?\s*\n(?<body>.*?)\n\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Fenced();

    /// <summary>Último bloco cercado por ``` que seja JSON válido; senão, o texto inteiro se for JSON; senão, null.</summary>
    public static string? ExtractLast(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var match in Fenced().Matches(text).Reverse())
        {
            var body = match.Groups["body"].Value.Trim();
            if (IsValid(body)) return body;
        }

        var whole = text.Trim();
        return IsValid(whole) ? whole : null;
    }

    private static bool IsValid(string candidate)
    {
        if (candidate.Length == 0 || candidate[0] is not ('{' or '[')) return false;
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
