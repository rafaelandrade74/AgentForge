using AgentForge.Domain;

namespace AgentForge.Orchestrator.Workspaces;

/// <summary>
/// Resultado da resolução: <see cref="Url"/> é a URL de push (nula quando não há como publicar), <see cref="Token"/> o token do GitHub
/// guardado no projeto (nulo se não houver ou não abrir; necessário para abrir PR mesmo em SSH) e <see cref="Reason"/> o motivo, em
/// texto para a pessoa, de faltar a URL ou o token. Url e Token podem conter segredo: nunca logar nem devolver pela API.
/// </summary>
public sealed record PushUrlResult(string? Url, string? Token, string? Reason)
{
    /// <summary>Nunca imprime Url nem Token: evita vazar segredo em log ou exceção.</summary>
    public override string ToString() =>
        $"PushUrlResult {{ Url = {(Url is null ? "null" : "***")}, Token = {(Token is null ? "null" : "***")}, Reason = {Reason} }}";
}

/// <summary>
/// Resolve a URL usada para empurrar o branch de uma tarefa: a URL SSH do projeto (mesma chave do pull) ou, em projetos HTTPS do
/// GitHub, a URL com o token do projeto. Sem URL (HTTPS sem token, ou repositório fora do GitHub), <see cref="PushUrlResult.Url"/> é nula.
/// </summary>
public interface IPushUrlProvider
{
    Task<PushUrlResult> ResolveAsync(Project project, CancellationToken ct = default);
}
