using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentForge.Orchestrator.Agents;

/// <summary>
/// Reconhece, na saída do Claude Code, que a execução falhou por limite de uso e quando libera. Formas conhecidas:
/// evento <c>rate_limit_event</c> com status "rejected" e <c>resetsAt</c> (segundos desde 1970); resultado
/// "Claude AI usage limit reached|&lt;epoch&gt;"; e "You've hit your limit · resets 3pm (America/Sao_Paulo)".
/// A leitura é tolerante: se não houver horário, o limite vale como "sem reset conhecido" (sonda periódica).
/// </summary>
public static partial class UsageLimitParser
{
    [GeneratedRegex(@"limit reached\|\s*(?<epoch>\d{9,13})", RegexOptions.IgnoreCase)]
    private static partial Regex PipeEpoch();

    [GeneratedRegex(@"resets?\s+(?:at\s+)?(?:(?<mon>[A-Za-z]{3,9})\.?\s+(?<day>\d{1,2})(?:,|\s+at)?\s*)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm)?\s*(?:\((?<tz>[^)]+)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex ResetClock();

    [GeneratedRegex(@"hit your (?:\w+\s+)?limit|usage limit|limit reached|rate[ _]limit|too many requests", RegexOptions.IgnoreCase)]
    private static partial Regex LimitPhrase();

    /// <summary>Texto do resultado de erro que fala de limite de uso (sem olhar o horário).</summary>
    public static bool LooksLikeLimit(string text) => LimitPhrase().IsMatch(text);

    public static string KindOf(string text, string? rateLimitType = null)
    {
        var probe = (rateLimitType + " " + text).ToLowerInvariant();
        if (probe.Contains("seven_day") || probe.Contains("week")) return UsageLimitKinds.Weekly;
        if (probe.Contains("five_hour") || probe.Contains("5-hour") || probe.Contains("session") || probe.Contains("hit your limit") || probe.Contains("usage limit"))
            return UsageLimitKinds.Session;
        if (probe.Contains("rate limit") || probe.Contains("rate_limit") || probe.Contains("too many")) return UsageLimitKinds.Rate;
        return UsageLimitKinds.Unknown;
    }

    /// <summary>Horário de reset (UTC) dito no texto, se houver.</summary>
    public static DateTime? ResetFromText(string text, DateTime nowUtc)
    {
        if (PipeEpoch().Match(text) is { Success: true } pipe)
            return FromEpoch(long.Parse(pipe.Groups["epoch"].Value, CultureInfo.InvariantCulture));

        foreach (Match m in ResetClock().Matches(text))
        {
            // "resets 5" sem am/pm nem minutos é ambíguo (pode ser "5 hours"): ignora
            if (!m.Groups["ap"].Success && !m.Groups["m"].Success) continue;
            if (TryClock(m, nowUtc, out var utc)) return utc;
        }
        return null;
    }

    public static DateTime FromEpoch(long value) =>
        DateTimeOffset.FromUnixTimeSeconds(value > 99_999_999_999 ? value / 1000 : value).UtcDateTime;

    private static bool TryClock(Match m, DateTime nowUtc, out DateTime utc)
    {
        utc = default;
        var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (m.Groups["ap"].Success)
        {
            if (hour is < 1 or > 12) return false;
            hour = hour % 12 + (m.Groups["ap"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        }
        if (hour > 23 || minute > 59) return false;

        TimeZoneInfo zone = TimeZoneInfo.Utc;
        if (m.Groups["tz"].Success)
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(m.Groups["tz"].Value.Trim()); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Local; }
        }

        try
        {
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
            DateTime candidate;
            if (m.Groups["mon"].Success &&
                DateTime.TryParse($"{m.Groups["mon"].Value} {m.Groups["day"].Value} {nowLocal.Year}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                candidate = date.Date.AddHours(hour).AddMinutes(minute);
                if (candidate < nowLocal.AddDays(-1)) candidate = candidate.AddYears(1);   // "Jan 3" dito em dezembro
            }
            else
            {
                candidate = nowLocal.Date.AddHours(hour).AddMinutes(minute);
                if (candidate <= nowLocal) candidate = candidate.AddDays(1);
            }
            utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), zone);
            return true;
        }
        catch (ArgumentException)
        {
            return false;   // hora inexistente/ambígua na troca de horário de verão
        }
    }
}
