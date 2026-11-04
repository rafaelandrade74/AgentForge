using AgentForge.Api.Endpoints;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Api.GitHub;

public record GitHubListRequest(string? Token);

public record GitHubImportRequest(
    string? Repository, string? Token, bool AddDeployKey = true, string? Name = null,
    string? BuildCommand = null, string? TestCommand = null, bool AutoMode = false);

public static class GitHubEndpoints
{
    public static void MapGitHubEndpoints(this IEndpointRouteBuilder app)
    {
        // só com o login (JWT)
        var g = app.MapGroup("/api/github").RequireAuthorization().WithTags("GitHub");

        g.MapPost("/repos", async (GitHubListRequest req, GitHubClient github, CancellationToken ct) =>
        {
            if (!ValidToken(req.Token)) return Results.ValidationProblem(Problem("token", "Informe o token de acesso do GitHub."));
            try
            {
                var repos = await github.ListReposAsync(req.Token!, ct);
                return Results.Ok(repos);
            }
            catch (GitHubException ex)
            {
                return Failure(ex);
            }
        }).WithSummary("Lista os repositórios que o token enxerga. O token é usado só nesta chamada e não é guardado.");

        g.MapPost("/import", async (GitHubImportRequest req, GitHubClient github, GitCredentialService git, IWorkspaceManager workspaces,
            AppDbContext db, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("GitHubImport");
            var fullName = GitHubClient.ParseFullName(req.Repository);
            if (fullName is null)
                return Results.ValidationProblem(Problem("repository", "Informe o repositório como dono/nome ou a URL do GitHub."));
            var token = string.IsNullOrWhiteSpace(req.Token) ? null : req.Token.Trim();
            if (token is not null && !ValidToken(token)) return Results.ValidationProblem(Problem("token", "Token inválido."));

            try
            {
                var repo = await github.GetRepoAsync(token, fullName, ct);
                var url = repo.Private ? repo.SshUrl : repo.CloneUrl;
                if (await db.Projects.AnyAsync(p => p.RepositoryUrl == url || p.RepositoryUrl == repo.CloneUrl || p.RepositoryUrl == repo.SshUrl, ct))
                    return Results.Conflict(new { error = $"O repositório {repo.FullName} já está cadastrado como projeto." });

                var notes = new List<string>();
                var deployKeyAdded = false;
                if (repo.Private) deployKeyAdded = await PrepareAccessAsync(req, token, repo, github, git, notes, ct);

                var project = new Project
                {
                    Name = string.IsNullOrWhiteSpace(req.Name) ? repo.Name : req.Name.Trim(),
                    RepositoryUrl = url, BaseBranch = repo.DefaultBranch,
                    BuildCommand = req.BuildCommand ?? "", TestCommand = req.TestCommand ?? "", AutoMode = req.AutoMode,
                };
                db.Projects.Add(project);
                await db.SaveChangesAsync(ct);

                // "importar" já clona: erro de acesso aparece agora, não na primeira demanda
                var (cloned, cloneError) = await TryCloneAsync(workspaces, project, retry: repo.Private && deployKeyAdded, log, ct);
                if (!cloned)
                {
                    db.Projects.Remove(project);
                    await db.SaveChangesAsync(CancellationToken.None);
                    return Results.Json(new { error = $"O projeto não foi criado: não foi possível clonar. {cloneError}", notes }, statusCode: StatusCodes.Status502BadGateway);
                }

                return Results.Ok(new { project = CrudEndpoints.ToDto(project), cloned = true, deployKeyAdded, notes, repository = repo });
            }
            catch (GitHubException ex)
            {
                return Failure(ex);
            }
        }).WithSummary("Cadastra um repositório do GitHub como projeto e já o clona (gera/autoriza a chave SSH, se for privado).");
    }

    /// <summary>Repositório privado: garante a chave SSH e, se pedido, registra-a como deploy key somente leitura.</summary>
    private static async Task<bool> PrepareAccessAsync(GitHubImportRequest req, string? token, GitHubRepo repo, GitHubClient github,
        GitCredentialService git, List<string> notes, CancellationToken ct)
    {
        var status = await git.GetStatusAsync(ct);
        if (!status.Supported)
        {
            notes.Add("O git roda no seu computador: o clone usa as credenciais que você já configurou lá.");
            return false;
        }

        if (!status.HasKey)
        {
            status = await git.GenerateAsync(overwrite: false, ct);
            notes.Add("Chave SSH do AgentForge gerada.");
        }

        if (!req.AddDeployKey || token is null)
        {
            notes.Add("Autorize a chave pública no GitHub (Deploy key do repositório, somente leitura) — veja a tela Git.");
            return false;
        }

        var result = await github.AddDeployKeyAsync(token, repo.FullName, "AgentForge (somente leitura)", status.PublicKey!, ct);
        notes.Add(result == DeployKeyResult.Added
            ? "Chave registrada como deploy key somente leitura."
            : "A chave já estava registrada no GitHub.");
        return true;
    }

    private static async Task<(bool Ok, string? Error)> TryCloneAsync(IWorkspaceManager workspaces, Project project, bool retry, ILogger log, CancellationToken ct)
    {
        string? error = null;
        for (var attempt = 0; attempt < (retry ? 2 : 1); attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(3), ct);   // a deploy key pode levar um instante para valer
            try
            {
                await workspaces.EnsureRepositoryAsync(project, ct);
                return (true, null);
            }
            catch (GitException ex)
            {
                error = Friendly(ex.Message);
                log.LogWarning("Clone do projeto {Name} falhou (tentativa {Attempt})", project.Name, attempt + 1);
            }
        }
        return (false, error);
    }

    private static string Friendly(string gitMessage) =>
        gitMessage.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || gitMessage.Contains("publickey", StringComparison.OrdinalIgnoreCase)
            ? "O git não tem acesso ao repositório: a chave SSH ainda não está autorizada no GitHub."
            : gitMessage;

    private static bool ValidToken(string? token) =>
        token is { Length: >= 20 and <= 400 } && token.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c));

    private static Dictionary<string, string[]> Problem(string field, string message) => new() { [field] = [message] };

    private static IResult Failure(GitHubException ex) => Results.Json(new { error = ex.Message },
        statusCode: ex.Status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden
            ? (int)ex.Status : StatusCodes.Status502BadGateway);
}
