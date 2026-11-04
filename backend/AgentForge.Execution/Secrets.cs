using System.Text.RegularExpressions;

namespace AgentForge.Execution;

/// <summary>Evita que credenciais saiam do servidor: em URLs (https://token@host) e no ambiente dos processos filhos.</summary>
public static partial class Secrets
{
    public const string Redacted = "***";

    [GeneratedRegex(@"(?<scheme>https?://)[^/\s@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UserInfo();

    /// <summary>Troca a credencial embutida em URLs http(s) por ***: https://TOKEN@github.com/x → https://***@github.com/x.</summary>
    public static string MaskUrls(string? text) =>
        string.IsNullOrEmpty(text) ? text ?? "" : UserInfo().Replace(text, "${scheme}" + Redacted + "@");

    public static bool IsMasked(string? text) => text is not null && text.Contains("://" + Redacted + "@", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] SecretPrefixes = ["ConnectionStrings__", "Auth__", "Agents__", "HostAgent__"];

    private static readonly string[] SecretNames =
    [
        "ADMIN_USERNAME", "ADMIN_PASSWORD", "JWT_SECRET", "POSTGRES_USER", "POSTGRES_PASSWORD", "DATABASE_URL",
        "NATIVE_DB_CONNECTION", "HOST_AGENT_TOKEN",
    ];

    public static bool IsSecretVariable(string name) =>
        SecretNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        SecretPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Remove do ambiente de um processo filho os segredos da própria aplicação (senha do banco, JWT, admin, token do host
    /// agent). O agente tem Bash: sem isto, um `env` mostraria tudo isso. As credenciais do Claude (ANTHROPIC_*, CLAUDE_*) ficam.
    /// </summary>
    public static void ScrubEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(IsSecretVariable).ToList()) environment.Remove(name);
    }
}
