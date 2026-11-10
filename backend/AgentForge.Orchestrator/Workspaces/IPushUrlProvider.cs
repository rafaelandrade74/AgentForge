using AgentForge.Domain;

namespace AgentForge.Orchestrator.Workspaces;

/// <summary>
/// Resolve a URL usada para empurrar o branch de uma tarefa: a URL SSH do projeto (mesma chave do pull) ou, em projetos HTTPS do
/// GitHub, a URL com o token do projeto. Devolve nulo quando não há como publicar (HTTPS sem token, ou repositório fora do GitHub).
/// O resultado pode conter segredo: nunca logar nem devolver pela API.
/// </summary>
public interface IPushUrlProvider
{
    Task<string?> ResolveAsync(Project project, CancellationToken ct = default);
}
