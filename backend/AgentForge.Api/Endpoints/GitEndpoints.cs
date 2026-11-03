using AgentForge.Orchestrator.Workspaces;

namespace AgentForge.Api.Endpoints;

public record GitAccessRequest(string? RepositoryUrl);
public record GitGenerateRequest(bool Overwrite = false);

public static class GitEndpoints
{
    public static void MapGitEndpoints(this IEndpointRouteBuilder app)
    {
        // só com o login (JWT): um token de acesso do MCP não gera nem apaga chaves
        var g = app.MapGroup("/api/git/ssh").RequireAuthorization().WithTags("Git");

        g.MapGet("/", (GitCredentialService s, CancellationToken ct) => s.GetStatusAsync(ct))
            .WithSummary("Estado da chave SSH do AgentForge (só a chave pública é devolvida).");

        g.MapPost("/generate", async (GitGenerateRequest? body, GitCredentialService s, CancellationToken ct) =>
            await Handle(() => s.GenerateAsync(body?.Overwrite ?? false, ct)))
            .WithSummary("Gera uma chave ed25519 no servidor. A privada nunca sai dele.");

        g.MapDelete("/", async (GitCredentialService s, CancellationToken ct) =>
            await Handle(async () => { await s.DeleteAsync(ct); return await s.GetStatusAsync(ct); }));

        g.MapPost("/test", (GitAccessRequest req, GitCredentialService s, CancellationToken ct) =>
                s.TestAccessAsync(req.RepositoryUrl ?? "", ct))
            .WithSummary("Testa o acesso a um repositório (git ls-remote).");
    }

    private static async Task<IResult> Handle<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (GitCredentialException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }
}
