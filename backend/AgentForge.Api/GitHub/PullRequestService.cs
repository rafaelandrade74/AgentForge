using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Api.GitHub;

public record PushResult(string Branch, DateTime PushedAt);

public record PullRequestResult(string Url, int Number, string Branch, string BaseBranch, bool AlreadyExisted);

/// <summary>Motivo pelo qual o PR não pôde ser aberto, já em texto para a pessoa (nunca contém o token).</summary>
public class PullRequestException(string message, int status = StatusCodes.Status409Conflict) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Depois que a tarefa passou por todas as etapas e foi aprovada (Done), publica o branch dela e abre o pull request. É uma ação
/// HUMANA (botão ou ferramenta MCP), nunca dos agentes: eles continuam sem credencial de escrita. O push é feito pelo servidor, na
/// máquina do ambiente do projeto (onde está o clone), com o token do projeto e refspec fixo — só o branch da tarefa, nunca a base.
/// </summary>
public sealed partial class PullRequestService(
    AppDbContext db, IWorkspaceManager workspaces, ISecretProtector protector, IPushUrlProvider pushUrls, GitHubClient github, ILogger<PullRequestService> logger)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$")]
    private static partial Regex BranchPattern();

    /// <summary>Repositório acessado por SSH (git@... ou ssh://): o push usa a mesma chave do clone, sem token.</summary>
    public static bool UsesSsh(Project project) => PushUrlProvider.UsesSsh(project);

    public static bool IsValidBranchName(string? name) =>
        name is not null && BranchPattern().IsMatch(name) && !name.Contains("..") && !name.EndsWith('/') && !name.EndsWith(".lock") && !name.Contains("//");

    /// <summary>O destino efetivo: o pedido, senão o configurado no projeto, senão a branch base.</summary>
    public static string TargetBranch(Project project, string? requested) =>
        !string.IsNullOrWhiteSpace(requested) ? requested.Trim()
        : !string.IsNullOrWhiteSpace(project.PullRequestBaseBranch) ? project.PullRequestBaseBranch.Trim()
        : project.BaseBranch;

    public async Task<PullRequestResult> OpenAsync(Guid workItemId, string? requestedBase, CancellationToken ct)
    {
        var item = await db.WorkItems.Include(w => w.Dependencies).FirstOrDefaultAsync(w => w.Id == workItemId, ct)
                   ?? throw new PullRequestException("Tarefa não encontrada.", StatusCodes.Status404NotFound);
        var project = await db.Projects.FirstAsync(p => p.Id == db.Demands.First(d => d.Id == item.DemandId).ProjectId, ct);
        var branch = WorkspaceManager.BranchName(item.Id);

        if (item.State != WorkItemState.Done)
            throw new PullRequestException("O PR só pode ser aberto depois que a tarefa passou por todas as etapas e foi aprovada (Done).");

        var target = TargetBranch(project, requestedBase);
        if (!IsValidBranchName(target)) throw new PullRequestException("Branch de destino inválida.");
        if (target == branch) throw new PullRequestException("A branch de destino não pode ser a da própria tarefa.");

        if (item.PullRequestUrl is { Length: > 0 } existingUrl && item.PullRequestNumber is { } existingNumber)
            return new PullRequestResult(existingUrl, existingNumber, branch, target, AlreadyExisted: true);

        var fullName = GitHubClient.ParseFullName(project.RepositoryUrl)
                       ?? throw new PullRequestException("Só é possível abrir PR de projetos hospedados no GitHub (github.com).");
        var token = ReadToken(project);

        // 1) publica SÓ o branch da tarefa, a partir do clone da máquina do projeto
        await PushBranchAsync(item, project, ct);

        // 2) abre o PR
        PullRequestInfo pr;
        try
        {
            pr = await github.CreatePullRequestAsync(token, fullName, item.Title, branch, target, Body(item, project), ct);
        }
        catch (GitHubException ex)
        {
            throw new PullRequestException(
                $"O branch foi publicado, mas o GitHub recusou o PR: {ex.Message} (ajuste e tente de novo: o push não se repete à toa).",
                StatusCodes.Status502BadGateway);
        }

        item.PullRequestUrl = pr.Url;
        item.PushedAt ??= DateTime.UtcNow;
        item.PushError = null;
        item.PullRequestNumber = pr.Number;
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("PR #{Number} da tarefa {Task} aberto para {Base}", pr.Number, item.Id, target);
        return new PullRequestResult(pr.Url, pr.Number, branch, target, pr.AlreadyExisted);
    }

    /// <summary>Botão "Publicar branch": faz o git push do branch de uma tarefa Done (as que concluíram antes da publicação automática, ou que falharam).</summary>
    public async Task<PushResult> PushAsync(Guid workItemId, CancellationToken ct)
    {
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct)
                   ?? throw new PullRequestException("Tarefa não encontrada.", StatusCodes.Status404NotFound);
        if (item.State != WorkItemState.Done)
            throw new PullRequestException("O branch só é publicado depois que a tarefa passou por todas as etapas e foi aprovada (Done).");
        var project = await db.Projects.FirstAsync(p => p.Id == db.Demands.First(d => d.Id == item.DemandId).ProjectId, ct);

        await PushBranchAsync(item, project, ct);
        return new PushResult(WorkspaceManager.BranchName(item.Id), item.PushedAt!.Value);
    }

    /// <summary>
    /// Empurra SÓ o branch da tarefa (refspec fixo) para o GitHub, pela máquina do ambiente do projeto, e registra em
    /// <c>PushedAt</c>. Vale tanto para o botão "Abrir PR" quanto para a publicação automática ao concluir a tarefa.
    /// </summary>
    public async Task PushBranchAsync(WorkItem item, Project project, CancellationToken ct)
    {
        var pushUrl = await pushUrls.ResolveAsync(project, ct);
        if (pushUrl is null)
        {
            throw new PullRequestException(GitHubClient.ParseFullName(project.RepositoryUrl) is null
                ? "Só é possível publicar projetos hospedados no GitHub (github.com)."
                : "Cadastre o token do GitHub no projeto (Editar projeto → Token do GitHub para PRs) para abrir pull requests.");
        }
        try
        {
            await workspaces.PushTaskBranchAsync(project, item.Id, pushUrl, ct);
        }
        catch (WorkspaceException ex)
        {
            throw new PullRequestException(ex.Message);
        }
        catch (GitException ex)
        {
            logger.LogWarning("Push do branch da tarefa {Task} falhou", item.Id);   // sem a mensagem: pode ecoar a URL
            throw new PullRequestException(ExplainPushFailure(ex.Message), StatusCodes.Status502BadGateway);
        }
        catch (Exception ex) when (ex is ProcessStartException or EnvironmentUnavailableException)
        {
            throw new PullRequestException(ex.Message, StatusCodes.Status502BadGateway);
        }

        item.PushedAt = DateTime.UtcNow;
        item.PushError = null;
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("Branch da tarefa {Task} publicado no GitHub", item.Id);
    }

    private string ReadToken(Project project)
    {
        if (string.IsNullOrEmpty(project.ProtectedGitHubToken))
            throw new PullRequestException("Cadastre o token do GitHub no projeto (Editar projeto → Token do GitHub para PRs) para abrir pull requests.");
        try
        {
            return protector.Unprotect(project.ProtectedGitHubToken);
        }
        catch (CryptographicException)
        {
            throw new PullRequestException("Não foi possível ler o token do GitHub guardado (a chave de proteção mudou?). Edite o projeto e informe o token de novo.");
        }
    }

    private static string ExplainPushFailure(string gitMessage) =>
        gitMessage.Contains("read only", StringComparison.OrdinalIgnoreCase) || gitMessage.Contains("read-only", StringComparison.OrdinalIgnoreCase)
            ? "A chave SSH só tem permissão de leitura neste repositório. No GitHub (Settings → Deploy keys), apague a chave e adicione de novo marcando \"Allow write access\", ou use uma chave da sua conta."
        : gitMessage.Contains("Permission denied (publickey)", StringComparison.OrdinalIgnoreCase) || gitMessage.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
            ? "O GitHub não aceitou a chave SSH deste servidor (não autorizada neste repositório). Autorize a chave pública da tela Git."
        : gitMessage.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
        || gitMessage.Contains("Invalid username or token", StringComparison.OrdinalIgnoreCase)
        || gitMessage.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            ? "O GitHub recusou o token no push (inválido, expirado ou sem permissão de escrita em Contents neste repositório)."
        : gitMessage.Contains("403", StringComparison.Ordinal) || gitMessage.Contains("Permission to", StringComparison.OrdinalIgnoreCase)
            ? "O token não tem permissão de escrita (Contents) neste repositório."
        : gitMessage.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) || gitMessage.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            ? "O GitHub já tem um branch com este nome e outro histórico (push rejeitado). Apague-o no GitHub ou resolva manualmente."
        : "Não foi possível publicar o branch no GitHub: " + Secrets.MaskUrls(gitMessage);

    private static string Body(WorkItem item, Project project)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.Description)) sb.AppendLine(item.Description.Trim()).AppendLine();
        if (!string.IsNullOrWhiteSpace(item.AcceptanceCriteria)) sb.AppendLine("**Critérios de aceite**").AppendLine(item.AcceptanceCriteria.Trim()).AppendLine();
        if (item.Dependencies.Count > 0)
            sb.AppendLine("> Este branch já contém o trabalho das tarefas das quais depende: " +
                          string.Join(", ", item.Dependencies.Select(d => $"\"{d.Title}\"")) + ".").AppendLine();
        sb.AppendLine($"Passou por Dev, Tech Lead, Security e QA e foi aprovada por uma pessoa no AgentForge (projeto {project.Name}).");
        return sb.ToString().TrimEnd();
    }
}
