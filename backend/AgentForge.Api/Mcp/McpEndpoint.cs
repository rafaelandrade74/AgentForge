using System.Text.Json;
using System.Text.Json.Nodes;
using AgentForge.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace AgentForge.Api.Mcp;

public static class McpEndpoint
{
    public const string Path = "/mcp";

    public static void MapMcp(this IEndpointRouteBuilder app)
    {
        // aceita o JWT do login e os tokens de acesso (afk_...); só /mcp aceita os tokens de acesso
        var auth = new AuthorizeAttribute { AuthenticationSchemes = $"{JwtBearerDefaults.AuthenticationScheme},{ApiTokens.Scheme}" };

        app.MapPost(Path, async (HttpContext ctx, McpServer server) =>
        {
            if (ctx.Request.ContentLength > McpServer.MaxBodyBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            JsonNode? body;
            try
            {
                using var limited = new MemoryStream();
                await ctx.Request.Body.CopyToAsync(limited, ctx.RequestAborted);
                if (limited.Length > McpServer.MaxBodyBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                limited.Position = 0;
                body = await JsonNode.ParseAsync(limited, cancellationToken: ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.Json(McpServer.Error(null, -32700, "JSON inválido."), statusCode: StatusCodes.Status400BadRequest);
            }
            if (body is null) return Results.Json(McpServer.Error(null, -32600, "Corpo vazio."), statusCode: StatusCodes.Status400BadRequest);

            var response = await server.HandleAsync(body, ctx.RequestServices, ctx.RequestAborted);
            return response is null
                ? Results.Accepted()                      // só notificações: sem corpo
                : Results.Content(response.ToJsonString(), "application/json");
        }).RequireAuthorization(auth).WithTags("MCP").WithSummary("Servidor MCP (Streamable HTTP, JSON-RPC 2.0).");

        // sem fluxo servidor→cliente e sem sessões: GET/DELETE não se aplicam
        app.MapMethods(Path, ["GET", "DELETE"], () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed))
            .RequireAuthorization(auth).ExcludeFromDescription();
    }
}
