using System.Security.Cryptography;
using AgentForge.Domain;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Workspaces;

namespace AgentForge.Api.GitHub;

/// <summary>SSH: a URL do projeto. HTTPS com token: https://x-access-token:&lt;token&gt;@github.com/&lt;dono/repo&gt;.git. Senão, sem URL e com o motivo.</summary>
public sealed class PushUrlProvider(ISecretProtector protector) : IPushUrlProvider
{
    private const string NotGitHub = "Só é possível publicar projetos hospedados no GitHub (github.com).";
    private const string NoToken = "Cadastre o token do GitHub no projeto (Editar projeto → Token do GitHub) ou use a URL SSH do repositório.";
    private const string BadToken = "Não foi possível ler o token do GitHub guardado (a chave de proteção mudou?). Edite o projeto e informe o token de novo.";

    /// <summary>Repositório acessado por SSH (git@... ou ssh://): o push usa a mesma chave do clone, sem token.</summary>
    private static bool UsesSsh(Project project) =>
        project.RepositoryUrl.StartsWith("git@", StringComparison.Ordinal) || project.RepositoryUrl.StartsWith("ssh://", StringComparison.Ordinal);

    public Task<PushUrlResult> ResolveAsync(Project project, CancellationToken ct = default)
    {
        var (token, tokenReason) = ReadToken(project);
        if (UsesSsh(project)) return Task.FromResult(new PushUrlResult(project.RepositoryUrl.Trim(), token, tokenReason));

        var fullName = GitHubClient.ParseFullName(project.RepositoryUrl);
        if (fullName is null) return Task.FromResult(new PushUrlResult(null, token, NotGitHub));
        if (token is null) return Task.FromResult(new PushUrlResult(null, null, tokenReason));
        return Task.FromResult(new PushUrlResult($"https://x-access-token:{token}@github.com/{fullName}.git", token, null));
    }

    private (string? Token, string? Reason) ReadToken(Project project)
    {
        if (string.IsNullOrEmpty(project.ProtectedGitHubToken)) return (null, NoToken);
        try
        {
            return (protector.Unprotect(project.ProtectedGitHubToken), null);
        }
        catch (CryptographicException)
        {
            return (null, BadToken);
        }
    }
}
