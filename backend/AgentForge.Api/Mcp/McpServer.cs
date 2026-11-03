using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentForge.Api.Mcp;

/// <summary>
/// Servidor MCP (Model Context Protocol) sobre HTTP, no modo "Streamable HTTP" sem sessão e sem fluxo SSE: cada POST traz
/// uma mensagem (ou um lote) JSON-RPC 2.0 e a resposta volta como application/json. Suporta initialize, ping e tools/*.
/// </summary>
public sealed class McpServer(McpToolRegistry tools, ILogger<McpServer> logger)
{
    public const string ServerName = "agentforge";
    public const string ServerVersion = "1.0.0";
    public const int MaxBodyBytes = 512 * 1024;

    private static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string Instructions =
        "AgentForge orquestra agentes de IA (Scrum Master, Dev, Tech Lead, Security, QA) que desenvolvem tarefas em branches " +
        "isoladas. Fluxo: create_project → create_demand (start=true) → o Scrum Master propõe um plano → approve_plan → as tarefas " +
        "andam sozinhas até 'AwaitingApproval' → revise com get_task e get_task_diff → approve_task ou reject_task (com comentário). " +
        "Tarefas 'Blocked' esperam retry_task ou cancel_task. Use list_pending_actions para ver o que depende de você. " +
        "Nenhuma ferramenta faz push ou merge: o trabalho fica no branch agentforge/task-<id>.";

    /// <summary>Processa o corpo de um POST. Devolve null quando não há resposta (só notificações): HTTP 202.</summary>
    public async Task<JsonNode?> HandleAsync(JsonNode body, IServiceProvider services, CancellationToken ct)
    {
        if (body is JsonArray batch)
        {
            if (batch.Count == 0) return Error(null, -32600, "Lote vazio.");
            var responses = new JsonArray();
            foreach (var item in batch)
                if (await HandleOneAsync(item, services, ct) is { } response) responses.Add(response);
            return responses.Count == 0 ? null : responses;
        }
        return await HandleOneAsync(body, services, ct);
    }

    private async Task<JsonNode?> HandleOneAsync(JsonNode? message, IServiceProvider services, CancellationToken ct)
    {
        if (message is not JsonObject obj || obj["jsonrpc"]?.GetValue<string>() != "2.0" || obj["method"] is not JsonValue methodNode)
            return Error(IdOf(message), -32600, "Requisição JSON-RPC 2.0 inválida.");

        var method = methodNode.GetValue<string>();
        var id = obj["id"];
        var isNotification = id is null;
        var parameters = obj["params"] as JsonObject;

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = tools.Describe() },
                "tools/call" => await CallToolAsync(parameters, services, ct),
                _ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
                _ => throw new McpProtocolException(-32601, $"Método '{method}' não suportado."),
            };
            return isNotification ? null : Result(id, result ?? new JsonObject());
        }
        catch (McpProtocolException ex)
        {
            return isNotification ? null : Error(id, ex.Code, ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            // se o cliente pediu uma versão que conhecemos, respondemos com ela; senão, a mais nova nossa
            ["protocolVersion"] = requested is not null && SupportedVersions.Contains(requested) ? requested : SupportedVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "AgentForge", ["version"] = ServerVersion },
            ["instructions"] = Instructions,
        };
    }

    private async Task<JsonNode> CallToolAsync(JsonObject? parameters, IServiceProvider services, CancellationToken ct)
    {
        var name = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) throw new McpProtocolException(-32602, "Informe o nome da ferramenta (params.name).");
        var tool = tools.Find(name) ?? throw new McpProtocolException(-32602, $"Ferramenta '{name}' não existe.");
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            var output = await tool.Run(new ToolContext(arguments, services, ct));
            return ToolResult(output is string s ? s : JsonSerializer.Serialize(output, Json), isError: false);
        }
        catch (ToolInputException ex)
        {
            // erro de uso da ferramenta: volta como resultado com isError, para o modelo corrigir e tentar de novo
            return ToolResult(ex.Message, isError: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro na ferramenta MCP {Tool}", name);
            return ToolResult("Erro interno ao executar a ferramenta.", isError: true);
        }
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result,
    };

    public static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static JsonNode? IdOf(JsonNode? message) => (message as JsonObject)?["id"];
}

public sealed class McpProtocolException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>Argumento inválido ou ação impossível: vira resultado com isError = true.</summary>
public sealed class ToolInputException(string message) : Exception(message);
