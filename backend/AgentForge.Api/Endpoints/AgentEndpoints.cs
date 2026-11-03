using AgentForge.Orchestrator.Agents;

namespace AgentForge.Api.Endpoints;

public record LoginCodeRequest(string? Code);

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/agents").RequireAuthorization().WithTags("Agents");

        g.MapGet("/", (AgentCliService s, CancellationToken ct) => s.ListAsync(ct))
            .WithSummary("Agentes disponíveis: instalado, conectado, em quais papéis é usado e o último teste.");
        g.MapGet("/environment", (AgentCliService s) =>
            {
                var (mode, description) = s.Environment;
                return new { mode, description };
            })
            .WithSummary("Onde os CLIs rodam: \"local\" (neste container/computador) ou \"host\" (computador, pelo host agent).");
        g.MapGet("/{name}", (string name, bool? refresh, AgentCliService s, CancellationToken ct) =>
            Handle(() => s.GetAsync(name, refresh ?? false, ct)));
        g.MapPost("/{name}/test", (string name, AgentCliService s, CancellationToken ct) => Handle(() => s.TestAsync(name, ct)))
            .WithSummary("Faz uma chamada real e mínima ao CLI para testar a conexão (não grava execução).");

        g.MapPost("/{name}/login", (string name, AgentCliService s, CancellationToken ct) => Handle(() => s.StartLoginAsync(name, ct)))
            .WithSummary("Inicia o login por link do CLI: devolve o link para a pessoa abrir.");
        g.MapGet("/{name}/login/{sessionId}", (string name, string sessionId, AgentCliService s) =>
            Handle(() => Task.FromResult(s.GetLogin(name, sessionId))));
        g.MapPost("/{name}/login/{sessionId}/code", (string name, string sessionId, LoginCodeRequest body, AgentCliService s, CancellationToken ct) =>
                Handle(() => s.SubmitCodeAsync(name, sessionId, body.Code ?? "", ct)))
            .WithSummary("Envia ao CLI o código que a página de login mostrou.");
        g.MapDelete("/{name}/login/{sessionId}", (string name, string sessionId, AgentCliService s) =>
            Handle(() => Task.FromResult(s.CancelLogin(name, sessionId))));
        g.MapPost("/{name}/logout", (string name, AgentCliService s, CancellationToken ct) => Handle(() => s.LogoutAsync(name, ct)));
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
    }
}
