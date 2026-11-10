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

    /// <summary>Por que não há URL de push para o projeto (texto para a pessoa; nunca contém o token).</summary>
    public static string UnavailableReason(Project project) =>
        GitHubClient.ParseFullName(project.RepositoryUrl) is null
            ? "Só é possível publicar projetos hospedados no GitHub (github.com)."
            : "Cadastre o token do GitHub no projeto (Editar projeto → Token do GitHub para PRs) para abrir pull requests.";

    /// <summary>Monta a URL a partir de um token já lido: SSH devolve a URL do projeto; HTTPS exige GitHub e token.</summary>
    public static string? Compose(Project project, string? token)
    {
        if (UsesSsh(project)) return project.RepositoryUrl.Trim();
        var fullName = GitHubClient.ParseFullName(project.RepositoryUrl);
        return fullName is null || string.IsNullOrEmpty(token) ? null : $"https://x-access-token:{token}@github.com/{fullName}.git";
    }

    /// <summary>Único ponto que descriptografa o token do projeto; falha com <see cref="PullRequestException"/> se não houver ou não abrir.</summary>
    public string ReadToken(Project project)
    {
        if (string.IsNullOrEmpty(project.ProtectedGitHubToken))
            throw new PullRequestException(UnavailableReason(project));
        try
        {
            return protector.Unprotect(project.ProtectedGitHubToken);
        }
        catch (CryptographicException)
        {
            throw new PullRequestException("Não foi possível ler o token do GitHub guardado (a chave de proteção mudou?). Edite o projeto e informe o token de novo.");
        }
    }

    public Task<string?> ResolveAsync(Project project, CancellationToken ct = default)
    {
        if (UsesSsh(project)) return Task.FromResult(Compose(project, null));
        if (GitHubClient.ParseFullName(project.RepositoryUrl) is null || string.IsNullOrEmpty(project.ProtectedGitHubToken))
            return Task.FromResult<string?>(null);
        return Task.FromResult(Compose(project, ReadToken(project)));
    }
}
