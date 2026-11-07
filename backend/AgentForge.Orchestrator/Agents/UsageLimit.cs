using System.Collections.Concurrent;

namespace AgentForge.Orchestrator.Agents;

/// <summary>
/// O agente recusou trabalhar porque a conta atingiu o limite de uso (janela de horas, semanal ou taxa).
/// <see cref="ResetsAt"/> é o horário em que o CLI disse que libera (nulo se não deu para ler);
/// <see cref="RetryAt"/> é quando vale tentar de novo (reset + folga, ou uma sonda periódica se o reset é desconhecido).
/// </summary>
public record UsageLimit(DateTime? ResetsAt, DateTime RetryAt, string Kind, string Message);

public static class UsageLimitKinds
{
    public const string Session = "session";   // janela de poucas horas
    public const string Weekly = "weekly";
    public const string Rate = "rate";         // 429 passageiro
    public const string Unknown = "unknown";
}

public static class UsageLimitRules
{
    /// <summary>Sem horário de reset: tenta de novo (uma execução de sonda) a cada tanto.</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);

    /// <summary>Folga depois do reset informado, para o servidor do agente liberar de fato.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(60);

    /// <summary>Reset mais distante que isso é tratado como ilegível.</summary>
    public static readonly TimeSpan MaxReset = TimeSpan.FromDays(8);

    public static UsageLimit Create(DateTime? resetsAtUtc, string kind, string message, DateTime nowUtc)
    {
        var text = message.Length > 400 ? message[..400] + "…" : message;
        if (resetsAtUtc is { } reset && reset > nowUtc && reset - nowUtc <= MaxReset)
            return new UsageLimit(reset, reset + Margin, kind, text);
        // sem horário confiável (ilegível, no passado ou longe demais): sonda periódica
        return new UsageLimit(null, nowUtc + ProbeInterval, kind, text);
    }
}

/// <summary>Um limite ainda em vigor, por ambiente (cada máquina tem a sua conta) e agente.</summary>
public record UsageLimitStatus(string EnvironmentKey, string Adapter, UsageLimit Limit);

/// <summary>
/// Memória (em processo) dos limites de uso conhecidos. Serve para não gastar execuções que vão falhar: enquanto o limite
/// vale, o runner nem inicia o CLI. Passado o <see cref="UsageLimit.RetryAt"/>, a próxima execução funciona como sonda:
/// se passar, o limite é limpo; se falhar de novo, é registrado outra vez. Reiniciar a API só custa uma sonda.
/// </summary>
public interface IUsageLimitTracker
{
    /// <summary>O limite em vigor, ou nulo (sem limite conhecido ou já na hora de sondar).</summary>
    UsageLimit? Active(string environmentKey, string adapter);

    void Record(string environmentKey, string adapter, UsageLimit limit);

    void Clear(string environmentKey, string adapter);

    IReadOnlyList<UsageLimitStatus> Snapshot();
}

public class UsageLimitTracker : IUsageLimitTracker
{
    private readonly ConcurrentDictionary<(string Env, string Adapter), UsageLimit> _limits = new();

    public UsageLimit? Active(string environmentKey, string adapter) =>
        _limits.TryGetValue((environmentKey, adapter), out var limit) && limit.RetryAt > DateTime.UtcNow ? limit : null;

    public void Record(string environmentKey, string adapter, UsageLimit limit) => _limits[(environmentKey, adapter)] = limit;

    public void Clear(string environmentKey, string adapter) => _limits.TryRemove((environmentKey, adapter), out _);

    public IReadOnlyList<UsageLimitStatus> Snapshot() =>
        _limits.Where(p => p.Value.RetryAt > DateTime.UtcNow)
            .Select(p => new UsageLimitStatus(p.Key.Env, p.Key.Adapter, p.Value))
            .OrderBy(s => s.Limit.RetryAt).ToList();
}

public static class UsageLimitText
{
    public static string KindLabel(string kind) => kind switch
    {
        UsageLimitKinds.Session => "limite da janela de uso",
        UsageLimitKinds.Weekly => "limite semanal",
        UsageLimitKinds.Rate => "limite de requisições",
        _ => "limite de uso",
    };

    /// <summary>Texto curto para o aviso da tarefa (o horário vai em UTC; as telas de status mostram no fuso do navegador).</summary>
    public static string WaitingReason(UsageLimit limit)
    {
        var when = limit.ResetsAt is { } reset
            ? $"libera por volta de {reset:dd/MM HH:mm} UTC"
            : $"sem horário de liberação conhecido; tenta de novo às {limit.RetryAt:HH:mm} UTC";
        return $"Aguardando o agente liberar ({KindLabel(limit.Kind)}; {when}): {limit.Message}";
    }
}
