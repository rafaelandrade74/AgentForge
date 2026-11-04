using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Environments;

namespace AgentForge.Api.Endpoints;

public record LoginCodeRequest(string? Code);

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        // ?environmentId=<id> escolhe a máquina (cada uma tem o seu login do CLI); sem ele vale o ambiente padrão do servidor.
        var g = app.MapGroup("/api/agents").RequireAuthorization().WithTags("Agents");

        g.MapGet("/", (Guid? environmentId, AgentCliService s, CancellationToken ct) => Handle(() => s.ListAsync(environmentId, ct)))
            .WithSummary("Agentes disponíveis no ambiente: instalado, conectado, em quais papéis é usado e o último teste.");
        g.MapGet("/environment", (Guid? environmentId, AgentCliService s, CancellationToken ct) =>
                Handle(async () =>
                {
                    var (mode, description) = await s.EnvironmentAsync(environmentId, ct);
                    return new { mode, description };
                }))
            .WithSummary("Onde os CLIs rodam: \"local\" (neste container/computador) ou \"host\" (computador, pelo host agent).");
        g.MapGet("/{name}", (string name, bool? refresh, Guid? environmentId, AgentCliService s, CancellationToken ct) =>
            Handle(() => s.GetAsync(environmentId, name, refresh ?? false, ct)));
        g.MapPost("/{name}/test", (string name, Guid? environmentId, AgentCliService s, CancellationToken ct) =>
                Handle(() => s.TestAsync(environmentId, name, ct)))
            .WithSummary("Faz uma chamada real e mínima ao CLI para testar a conexão (não grava execução).");

        g.MapPost("/{name}/login", (string name, Guid? environmentId, AgentCliService s, CancellationToken ct) =>
                Handle(() => s.StartLoginAsync(environmentId, name, ct)))
            .WithSummary("Inicia o login por link do CLI: devolve o link para a pessoa abrir.");
        g.MapGet("/{name}/login/{sessionId}", (string name, string sessionId, Guid? environmentId, AgentCliService s) =>
            Handle(() => Task.FromResult(s.GetLogin(environmentId, name, sessionId))));
        g.MapPost("/{name}/login/{sessionId}/code", (string name, string sessionId, Guid? environmentId, LoginCodeRequest body, AgentCliService s, CancellationToken ct) =>
                Handle(() => s.SubmitCodeAsync(environmentId, name, sessionId, body.Code ?? "", ct)))
            .WithSummary("Envia ao CLI o código que a página de login mostrou.");
        g.MapDelete("/{name}/login/{sessionId}", (string name, string sessionId, Guid? environmentId, AgentCliService s) =>
            Handle(() => Task.FromResult(s.CancelLogin(environmentId, name, sessionId))));
        g.MapPost("/{name}/logout", (string name, Guid? environmentId, AgentCliService s, CancellationToken ct) =>
            Handle(() => s.LogoutAsync(environmentId, name, ct)));
    }

    private static async Task<IResult> Handle<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (AgentNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (AgentNotSupportedException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
        catch (EnvironmentUnavailableException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }
}
