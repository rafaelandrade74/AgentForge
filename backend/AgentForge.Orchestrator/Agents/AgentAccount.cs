namespace AgentForge.Orchestrator.Agents;

public record AgentAuthState(bool Authenticated, string? Method, string? Detail);

/// <summary>
/// Capacidade opcional de um adapter: dizer se o CLI está instalado e conectado e conduzir o login "por link".
/// O login é sempre do próprio CLI (que guarda a credencial no diretório dele); a aplicação só repassa o link e o
/// código que a pessoa cola, sem ler nem guardar token.
/// </summary>
public interface ICliAgentAccount
{
    string DisplayName { get; }
    IReadOnlyList<string> VersionArguments { get; }
    IReadOnlyList<string> StatusArguments { get; }
    AgentAuthState ParseStatus(string stdout, int exitCode);

    /// <summary>Comando que inicia o login e espera o código no stdin; null se o CLI não suporta.</summary>
    IReadOnlyList<string>? LoginArguments { get; }
    /// <summary>Extrai o link de autorização de uma linha da saída do login (null se a linha não tem).</summary>
    string? ExtractLoginUrl(string line);
    IReadOnlyList<string>? LogoutArguments { get; }

    /// <summary>Pedido curtíssimo usado para testar a conexão de verdade.</summary>
    string TestPrompt { get; }
}

public record AgentTestResult(bool Ok, string Message, long DurationMs, decimal CostUsd, DateTime At);

public record AgentInfo(
    string Name,
    string DisplayName,
    bool Installed,
    string? Version,
    bool Authenticated,
    string? AuthMethod,
    string? Detail,
    bool SupportsLogin,
    IReadOnlyList<string> UsedByRoles,
    AgentTestResult? LastTest);

public enum LoginState { WaitingForCode, Succeeded, Failed, Cancelled }

public record LoginSessionInfo(string SessionId, string? Url, LoginState State, string? Message);
