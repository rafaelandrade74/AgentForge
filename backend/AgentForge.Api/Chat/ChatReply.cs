using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentForge.Api.Chat;

/// <summary>Uma demanda que o agente propôs na conversa. <c>DemandId</c> preenchido = a pessoa já criou.</summary>
public record DemandProposal(string Title, string Description, Guid? DemandId = null);

/// <summary>Separa a resposta do agente do bloco JSON final <c>{"demands":[...]}</c> (as propostas), que a tela mostra como botões.</summary>
public static partial class ChatReply
{
    public const int MaxProposals = 5;
    public const int MaxTitle = 300;

    [GeneratedRegex(@"\n?```(?:json)?\s*\n(?<body>.*?)\n\s*```\s*$", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TrailingFence();

    public static (string Text, List<DemandProposal> Proposals) Split(string output, string? json)
    {
        var proposals = Parse(json);
        if (proposals.Count == 0) return (output.Trim(), proposals);

        // tira o bloco JSON do fim do texto (ele já virou botões); se o texto inteiro era o JSON, sobra só uma frase
        var text = TrailingFence().Replace(output.TrimEnd(), "").Trim();
        if (text.Length == 0 || text.StartsWith('{')) text = $"Propus {proposals.Count} demanda(s). Revise e crie as que fizerem sentido.";
        return (text, proposals);
    }

    public static List<DemandProposal> Parse(string? json)
    {
        var result = new List<DemandProposal>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("demands", out var list) || list.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in list.EnumerateArray())
            {
                if (result.Count >= MaxProposals) break;
                if (item.ValueKind != JsonValueKind.Object) continue;
                var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
                if (title.Length == 0) continue;
                var description = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()!.Trim() : "";
                result.Add(new DemandProposal(title.Length > MaxTitle ? title[..MaxTitle] : title, description));
            }
        }
        catch (JsonException)
        {
            // JSON que não é o esperado: sem propostas
        }
        return result;
    }
}
