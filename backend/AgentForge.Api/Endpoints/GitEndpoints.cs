using AgentForge.Orchestrator.Workspaces;

namespace AgentForge.Api.Endpoints;

public record GitAccessRequest(string? RepositoryUrl);
public record GitGenerateRequest(bool Overwrite = false);

public static class GitEndpoints
{
    public static void MapGitEndpoints(this IEndpointRouteBuilder app)
    {
        // só com o login (JWT): um token de acesso do MCP não gera nem apaga chaves.
        // ?environmentId=<id> escolhe a máquina; sem ele vale o ambiente padrão do servidor.
        var g = app.MapGroup("/api/git/ssh").RequireAuthorization().WithTags("Git");

        g.MapGet("/", (Guid? environmentId, GitCredentialService s, CancellationToken ct) =>
                Handle(() => s.GetStatusAsync(environmentId, ct)))
            .WithSummary("Estado da chave SSH do ambiente (só a chave pública é devolvida).");

        g.MapPost("/generate", (Guid? environmentId, GitGenerateRequest? body, GitCredentialService s, CancellationToken ct) =>
                Handle(() => s.GenerateAsync(environmentId, body?.Overwrite ?? false, ct)))
            .WithSummary("Gera uma chave ed25519 na máquina do ambiente. A privada nunca sai dela.");

        g.MapDelete("/", (Guid? environmentId, GitCredentialService s, CancellationToken ct) =>
            Handle(async () => { await s.DeleteAsync(environmentId, ct); return await s.GetStatusAsync(environmentId, ct); }));

        g.MapPost("/test", (Guid? environmentId, GitAccessRequest req, GitCredentialService s, CancellationToken ct) =>
                Handle(() => s.TestAccessAsync(environmentId, req.RepositoryUrl ?? "", ct)))
            .WithSummary("Testa o acesso a um repositório a partir da máquina do ambiente (git ls-remote).");
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
