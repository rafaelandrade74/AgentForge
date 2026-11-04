using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentForge.Api.GitHub;

public record GitHubRepo(
    string FullName, string Name, bool Private, string DefaultBranch, string? Description,
    string CloneUrl, string SshUrl, DateTime? UpdatedAt, bool CanAdmin);

public class GitHubException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public enum DeployKeyResult { Added, AlreadyRegistered }

/// <summary>
/// Cliente mínimo da API do GitHub. O token vem da requisição da pessoa, é usado só para a chamada e **nunca é guardado nem
/// registrado em log** (as mensagens de erro não o repetem).
/// </summary>
public sealed partial class GitHubClient(HttpClient http)
{
    private const int PageSize = 100;
    private const int MaxPages = 3;

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex FullNamePattern();

    public static bool IsValidFullName(string? fullName) =>
        fullName is { Length: <= 140 } && FullNamePattern().IsMatch(fullName) && !fullName.Contains("..");

    /// <summary>Aceita "owner/repo", "https://github.com/owner/repo(.git)" ou "git@github.com:owner/repo.git".</summary>
    public static string? ParseFullName(string? input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return null;
        s = Regex.Replace(s, @"^(https?://(www\.)?github\.com/|git@github\.com:|ssh://git@github\.com/)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(\.git)?/?$", "");
        return IsValidFullName(s) ? s : null;
    }

    public async Task<IReadOnlyList<GitHubRepo>> ListReposAsync(string token, CancellationToken ct)
    {
        var all = new List<GitHubRepo>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Request(HttpMethod.Get,
                $"user/repos?per_page={PageSize}&page={page}&sort=updated&affiliation=owner,collaborator,organization_member", token);
            var items = await SendAsync<List<RepoDto>>(request, ct) ?? [];
            all.AddRange(items.Select(ToRepo));
            if (items.Count < PageSize) break;
        }
        return all;
    }

    public async Task<GitHubRepo> GetRepoAsync(string? token, string fullName, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"repos/{fullName}", token);
        return ToRepo(await SendAsync<RepoDto>(request, ct) ?? throw new GitHubException(HttpStatusCode.BadGateway, "Resposta vazia do GitHub."));
    }

    /// <summary>Registra a chave pública como deploy key somente leitura. Se o GitHub disser que a chave já está em uso, segue.</summary>
    public async Task<DeployKeyResult> AddDeployKeyAsync(string token, string fullName, string title, string publicKey, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"repos/{fullName}/keys", token);
        request.Content = JsonContent.Create(new { title, key = publicKey, read_only = true });
        try
        {
            await SendAsync<JsonElement?>(request, ct);
            return DeployKeyResult.Added;
        }
        catch (GitHubException ex) when (ex.Status == HttpStatusCode.UnprocessableEntity && ex.Message.Contains("already in use", StringComparison.OrdinalIgnoreCase))
        {
            return DeployKeyResult.AlreadyRegistered;
        }
    }

    // ------------------------------------------------------------------------------------------------

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentForge", "1.0"));
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return request;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubException(HttpStatusCode.BadGateway, $"Não foi possível falar com o GitHub: {ex.Message}");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return response.Content.Headers.ContentLength == 0 ? default : await response.Content.ReadFromJsonAsync<T>(ct);

            var detail = await ReadMessageAsync(response, ct);
            throw new GitHubException(response.StatusCode, response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "O GitHub recusou o token (inválido, expirado ou sem permissão).",
                HttpStatusCode.NotFound => "Repositório não encontrado, ou o token não tem acesso a ele.",
                HttpStatusCode.Forbidden when response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0" =>
                    "Limite de requisições do GitHub atingido; tente de novo mais tarde.",
                HttpStatusCode.Forbidden => "O token não tem permissão para isso (para deploy key: repositório com permissão de administração).",
                _ => string.IsNullOrWhiteSpace(detail) ? $"O GitHub respondeu {(int)response.StatusCode}." : detail,
            });
        }
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var message = body.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (body.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                message += " " + string.Join(" ", errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em) ? em.GetString() : e.ToString()));
            return message.Trim();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return "";
        }
    }

    private static GitHubRepo ToRepo(RepoDto r) =>
        new(r.FullName, r.Name, r.Private, r.DefaultBranch ?? "main", r.Description, r.CloneUrl, r.SshUrl, r.UpdatedAt, r.Permissions?.Admin ?? false);

    private sealed record RepoDto(
        [property: JsonPropertyName("full_name")] string FullName,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("private")] bool Private,
        [property: JsonPropertyName("default_branch")] string? DefaultBranch,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("clone_url")] string CloneUrl,
        [property: JsonPropertyName("ssh_url")] string SshUrl,
        [property: JsonPropertyName("updated_at")] DateTime? UpdatedAt,
        [property: JsonPropertyName("permissions")] PermissionsDto? Permissions);

    private sealed record PermissionsDto([property: JsonPropertyName("admin")] bool Admin);
}
