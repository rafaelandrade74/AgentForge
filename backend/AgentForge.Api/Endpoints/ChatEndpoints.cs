using System.Text.Json;
using AgentForge.Api.Chat;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Workflow;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Api.Endpoints;

public record ChatCreateInput(Guid ProjectId, Guid? EnvironmentId = null, string? AdapterName = null, string? Title = null);
public record ChatSendInput(string? Text);
public record ProposalActionInput(bool Start = false);

public record ChatSessionDto(Guid Id, string Title, Guid ProjectId, string Project, Guid? EnvironmentId, string Environment, string Agent,
    bool Busy, DateTime CreatedAt, DateTime UpdatedAt);
public record ChatMessageDto(Guid Id, string Role, string Text, bool Failed, List<DemandProposal> Proposals, decimal CostUsd, DateTime CreatedAt);
public record ChatDetailDto(ChatSessionDto Session, List<ChatMessageDto> Messages);
public record ProposalCreatedDto(DemandDto Demand, bool Started);

/// <summary>Conversas com o agente de uma máquina (tela Chat). Tudo só com JWT; o agente propõe demandas e a pessoa cria.</summary>
public static class ChatEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chats").RequireAuthorization().WithTags("Chat");

        g.MapGet("/", async (AppDbContext db, ChatService chat, CancellationToken ct) =>
        {
            var sessions = await db.ChatSessions.AsNoTracking().OrderByDescending(s => s.UpdatedAt).Take(100).ToListAsync(ct);
            return await ToDtosAsync(db, chat, sessions, ct);
        }).WithSummary("Conversas, as mais recentes primeiro.");

        g.MapPost("/", async (ChatCreateInput input, AppDbContext db, ChatService chat, AgentAdapterRegistry adapters, CancellationToken ct) =>
        {
            if (!await db.Projects.AnyAsync(p => p.Id == input.ProjectId, ct)) return Invalid("projectId", "Escolha um projeto.");
            var adapterName = string.IsNullOrWhiteSpace(input.AdapterName) ? ClaudeCodeAdapter.AdapterName : input.AdapterName.Trim();
            if (adapters.TryGet(adapterName) is null) return Invalid("adapterName", $"Agente '{adapterName}' não existe.");
            if (input.EnvironmentId is { } env && !await db.ExecutionEnvironments.AnyAsync(e => e.Id == env, ct))
                return Invalid("environmentId", "Ambiente não encontrado.");
            var title = (input.Title ?? "").Trim();
            if (title.Length > 200) title = title[..200];

            var session = new ChatSession { ProjectId = input.ProjectId, EnvironmentId = input.EnvironmentId, AdapterName = adapterName, Title = title };
            db.ChatSessions.Add(session);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/chats/{session.Id}", (await ToDtosAsync(db, chat, [session], ct))[0]);
        }).WithSummary("Abre uma conversa com o agente de um ambiente, sobre um projeto.");

        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db, ChatService chat, CancellationToken ct) =>
        {
            if (await db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) is not { } session) return Results.NotFound();
            var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ChatSessionId == id).OrderBy(m => m.CreatedAt).ToListAsync(ct);
            return Results.Ok(new ChatDetailDto((await ToDtosAsync(db, chat, [session], ct))[0], messages.Select(ToDto).ToList()));
        });

        g.MapPost("/{id:guid}/messages", async (Guid id, ChatSendInput input, AppDbContext db, ChatService chat, CancellationToken ct) =>
        {
            var text = (input.Text ?? "").Trim();
            if (text.Length == 0) return Invalid("text", "Escreva a mensagem.");
            if (text.Length > ChatService.MaxMessageChars) return Invalid("text", $"A mensagem pode ter até {ChatService.MaxMessageChars} caracteres.");
            if (await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == id, ct) is not { } session) return Results.NotFound();
            if (session.Title.Length == 0)
            {
                session.Title = text.Length > 60 ? text[..60] + "…" : text;
                await db.SaveChangesAsync(ct);
            }
            try
            {
                return Results.Accepted($"/api/chats/{id}", ToDto(await chat.SendAsync(id, text, ct)));
            }
            catch (ChatBusyException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).WithSummary("Envia uma mensagem; a resposta do agente chega em segundo plano (SignalR chatChanged ou recarregando a conversa).");

        g.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, ChatService chat, CancellationToken ct) =>
        {
            if (chat.IsBusy(id)) return Results.Conflict(new { error = "Espere o agente terminar de responder para apagar a conversa." });
            return await db.ChatSessions.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
        });

        g.MapPost("/{id:guid}/messages/{messageId:guid}/demands/{index:int}",
            async (Guid id, Guid messageId, int index, ProposalActionInput? input, AppDbContext db, WorkflowActions actions, CancellationToken ct) =>
            {
                if (await db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) is not { } session) return Results.NotFound();
                var message = await db.ChatMessages.FirstOrDefaultAsync(
                    m => m.Id == messageId && m.ChatSessionId == id && m.Role == ChatRole.Assistant, ct);
                if (message?.Proposals is null) return Results.NotFound();
                var proposals = JsonSerializer.Deserialize<List<DemandProposal>>(message.Proposals, Json) ?? [];
                if (index < 0 || index >= proposals.Count) return Results.NotFound();
                if (proposals[index].DemandId is { } existing) return Results.Conflict(new { error = "Esta demanda já foi criada.", demandId = existing });
                if (!await db.Projects.AnyAsync(p => p.Id == session.ProjectId, ct)) return Results.Conflict(new { error = "O projeto desta conversa não existe mais." });

                var proposal = proposals[index];
                var demand = new Demand { ProjectId = session.ProjectId, Title = proposal.Title, Description = proposal.Description, Status = DemandStatus.New };
                db.Demands.Add(demand);
                proposals[index] = proposal with { DemandId = demand.Id };
                message.Proposals = JsonSerializer.Serialize(proposals, Json);
                await db.SaveChangesAsync(ct);

                var started = input?.Start == true && await actions.StartDemandAsync(demand.Id) == ActionOutcome.Ok;
                if (started) demand = await db.Demands.AsNoTracking().FirstAsync(d => d.Id == demand.Id, ct);
                return Results.Created($"/api/demands/{demand.Id}", new ProposalCreatedDto(CrudEndpoints.ToDto(demand), started));
            }).WithSummary("Cria a demanda proposta pelo agente (e, se pedido, já inicia o refinamento). Ação humana.");
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static ChatMessageDto ToDto(ChatMessage m) =>
        new(m.Id, m.Role.ToString(), m.Text, m.Failed,
            m.Proposals is null ? [] : JsonSerializer.Deserialize<List<DemandProposal>>(m.Proposals, Json) ?? [], m.CostUsd, m.CreatedAt);

    private static async Task<List<ChatSessionDto>> ToDtosAsync(AppDbContext db, ChatService chat, List<ChatSession> sessions, CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var environments = await db.ExecutionEnvironments.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name, ct);
        return sessions.Select(s => new ChatSessionDto(
            s.Id, s.Title.Length == 0 ? "Nova conversa" : s.Title, s.ProjectId, projects.GetValueOrDefault(s.ProjectId, "(projeto removido)"),
            s.EnvironmentId,
            s.EnvironmentId is { } e ? environments.GetValueOrDefault(e, "(ambiente removido)") : "Padrão do servidor",
            s.AdapterName, chat.IsBusy(s.Id), s.CreatedAt, s.UpdatedAt)).ToList();
    }
}
