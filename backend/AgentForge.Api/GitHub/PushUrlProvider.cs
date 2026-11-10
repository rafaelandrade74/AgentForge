using System.Security.Cryptography;
using AgentForge.Domain;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Workspaces;

namespace AgentForge.Api.GitHub;

/// <summary>SSH: a URL do projeto. HTTPS com token: https://x-access-token:&lt;token&gt;@github.com/&lt;dono/repo&gt;.git. Senão, nulo.</summary>
public sealed class PushUrlProvider(ISecretProtector protector) : IPushUrlProvider
{
    /// <summary>Repositório acessado por SSH (git@... ou ssh://): o push usa a mesma chave do clone, sem token.</summary>
    public static bool UsesSsh(Project project) =>
        project.RepositoryUrl.StartsWith("git@", StringComparison.Ordinal) || project.RepositoryUrl.StartsWith("ssh://", StringComparison.Ordinal);

    public Task<string?> ResolveAsync(Project project, CancellationToken ct = default)
    {
        if (UsesSsh(project)) return Task.FromResult<string?>(project.RepositoryUrl.Trim());

        var fullName = GitHubClient.ParseFullName(project.RepositoryUrl);
        if (fullName is null || string.IsNullOrEmpty(project.ProtectedGitHubToken)) return Task.FromResult<string?>(null);

        string token;
        try
        {
            token = protector.Unprotect(project.ProtectedGitHubToken);
        }
        catch (CryptographicException)
        {
            throw new PullRequestException("Não foi possível ler o token do GitHub guardado (a chave de proteção mudou?). Edite o projeto e informe o token de novo.");
        }
        return Task.FromResult<string?>($"https://x-access-token:{token}@github.com/{fullName}.git");
    }
}
