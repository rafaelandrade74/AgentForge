using System.Collections.Concurrent;
using System.Text.Json;
using AgentForge.Api.Realtime;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Api.Chat;

public class ChatBusyException() : Exception("O agente ainda está respondendo a mensagem anterior.");

/// <summary>
/// Conversa com o agente (Claude Code) de uma máquina. Cada mensagem vira uma execução <c>claude -p --resume &lt;sessão&gt;</c> no ambiente
/// escolhido, em segundo plano; a resposta chega por SignalR ("chatChanged") e por REST. O agente conversa e PROPÕE demandas num bloco
/// JSON; só a pessoa as cria. Com acesso ao clone do projeto (mesmo ambiente), o agente pode LER o código (Read/Glob/Grep), nunca alterar.
/// </summary>
public sealed class ChatService(IServiceScopeFactory scopes, IHubContext<RunsHub> hub, ILogger<ChatService> logger)
{
    public static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(10);
    public const int MaxMessageChars = 8000;
    public const string ReadOnlyTools = "Read,Glob,Grep";

    private readonly ConcurrentDictionary<Guid, bool> _busy = new();

    public bool IsBusy(Guid sessionId) => _busy.ContainsKey(sessionId);

    /// <summary>Grava a mensagem da pessoa e dispara a resposta em segundo plano. <exception cref="ChatBusyException"/> se já há uma em andamento.</summary>
    public async Task<ChatMessage> SendAsync(Guid sessionId, string text, CancellationToken ct)
    {
        if (!_busy.TryAdd(sessionId, true)) throw new ChatBusyException();
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var message = new ChatMessage { ChatSessionId = sessionId, Role = ChatRole.User, Text = text.Trim() };
            db.ChatMessages.Add(message);
            await db.SaveChangesAsync(ct);
            await db.ChatSessions.Where(s => s.Id == sessionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, DateTime.UtcNow), ct);

            _ = Task.Run(() => RunTurnAsync(sessionId, message.Text));
            return message;
        }
        catch
        {
            _busy.TryRemove(sessionId, out _);
            throw;
        }
    }

    private async Task RunTurnAsync(Guid sessionId, string text)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.ChatSessions.FirstAsync(s => s.Id == sessionId);
            var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == session.ProjectId);

            // o agente lê o código do projeto só se o clone mora na mesma máquina da conversa
            string? repo = null;
            if (project.EnvironmentId == session.EnvironmentId)
            {
                try
                {
                    repo = await scope.ServiceProvider.GetRequiredService<IWorkspaceManager>().EnsureRepositoryAsync(project, CancellationToken.None);
                }
                catch (Exception ex) when (ex is WorkspaceException or GitException or ProcessStartException or EnvironmentUnavailableException)
                {
                    logger.LogWarning("Conversa {Session}: sem acesso ao clone do projeto ({Reason}); segue sem ler o código.", sessionId, ex.GetType().Name);
                }
            }

            var recentDemands = await db.Demands.AsNoTracking().Where(d => d.ProjectId == project.Id)
                .OrderByDescending(d => d.CreatedAt).Take(10).Select(d => d.Title).ToListAsync();
            var request = new AgentRequest(AgentRole.Dev, text, repo ?? "", session.AdapterName,
                SystemPrompt: ChatPrompt.Build(project, recentDemands, repo is not null),
                AllowedTools: repo is null ? "" : ReadOnlyTools, MaxTurns: 15, Timeout: TurnTimeout, Ephemeral: true,
                EnvironmentId: session.EnvironmentId, EnvironmentOverride: repo is null, ResumeSessionId: session.ProviderSessionId);

            var run = await scope.ServiceProvider.GetRequiredService<IAgentRunner>().RunAsync(request, CancellationToken.None);

            ChatMessage reply;
            if (run.Status == AgentRunStatus.Succeeded)
            {
                var (shown, proposals) = ChatReply.Split(run.Output, run.Json);
                reply = new ChatMessage
                {
                    ChatSessionId = sessionId, Role = ChatRole.Assistant, Text = shown, CostUsd = run.CostUsd,
                    Proposals = proposals.Count == 0 ? null : JsonSerializer.Serialize(proposals),
                };
                if (!string.IsNullOrEmpty(run.SessionId)) session.ProviderSessionId = run.SessionId;
            }
            else
            {
                reply = new ChatMessage
                {
                    ChatSessionId = sessionId, Role = ChatRole.Assistant, Failed = true, CostUsd = run.CostUsd,
                    Text = run.UsageLimit is { } limit
                        ? $"O agente está sem uso disponível agora ({UsageLimitText.KindLabel(limit.Kind)}). " +
                          (limit.ResetsAt is { } at ? $"Libera por volta de {at:dd/MM HH:mm} UTC. " : "Tente de novo em alguns minutos. ") + "Reenvie a mensagem depois."
                        : "Não consegui responder: " + (string.IsNullOrWhiteSpace(run.Output) ? "o agente terminou sem resposta." : Truncate(run.Output, 1500)),
                };
            }
            db.ChatMessages.Add(reply);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao responder a conversa {Session}", sessionId);
            await SaveFailureAsync(sessionId, ex);
        }
        finally
        {
            _busy.TryRemove(sessionId, out _);
            await NotifyAsync(sessionId);
        }
    }

    private async Task SaveFailureAsync(Guid sessionId, Exception ex)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChatMessages.Add(new ChatMessage
            {
                ChatSessionId = sessionId, Role = ChatRole.Assistant, Failed = true,
                Text = "Não consegui responder: " + (ex is EnvironmentUnavailableException ? ex.Message : "erro interno (veja o log da API)."),
            });
            await db.SaveChangesAsync();
        }
        catch (Exception inner)
        {
            logger.LogError(inner, "Não foi possível registrar a falha da conversa {Session}", sessionId);
        }
    }

    private async Task NotifyAsync(Guid sessionId)
    {
        try
        {
            await hub.Clients.Group(RunsHub.ChatGroup(sessionId)).SendAsync("chatChanged", new { sessionId });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao avisar a conversa {Session}", sessionId);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

public static class ChatPrompt
{
    public static string Build(Project project, IReadOnlyList<string> recentDemands, bool canReadCode)
    {
        var demands = recentDemands.Count == 0 ? "(nenhuma ainda)" : string.Join("; ", recentDemands.Select(t => $"\"{t}\""));
        var code = canReadCode
            ? "Você pode LER o código do projeto (Read, Glob, Grep) para responder com precisão. Nunca altere arquivos nem rode comandos."
            : "Você não tem acesso ao código do projeto nesta conversa; responda pelo que a pessoa contar.";
        return $$"""
            Você é o assistente do AgentForge, conversando em português com a pessoa que cuida do projeto "{{project.Name}}"
            (repositório {{Secrets.MaskUrls(project.RepositoryUrl)}}, branch base {{project.BaseBranch}}).
            Demandas recentes do projeto: {{demands}}.

            {{code}}

            Seu papel: tirar dúvidas, ajudar a refinar ideias e PROPOR demandas. Uma demanda é um pedido em linguagem natural que o
            AgentForge quebra em tarefas para outros agentes executarem. Você NÃO cria demandas: só propõe, e a pessoa cria com um clique.
            Quando a pessoa pedir (ou concordar em) uma ou mais demandas, escreva a resposta normalmente e TERMINE com um bloco assim,
            e nada depois dele:

            ```json
            {"demands":[{"title":"Título curto e claro","description":"O que fazer, por quê e como saber que ficou pronto."}]}
            ```

            No máximo {{ChatReply.MaxProposals}} demandas por resposta, cada uma independente e específica. Sem pedido de demanda, não inclua o bloco.
            """;
    }
}
