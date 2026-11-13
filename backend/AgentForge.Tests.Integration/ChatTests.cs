using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Api.Chat;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests.Integration;

public class ChatReplyTests
{
    private const string Block = "{\"demands\":[{\"title\":\"Filtro\",\"description\":\"d\"}]}";

    [Fact]
    public void The_trailing_json_block_becomes_proposals_and_leaves_the_text()
    {
        var output = $"Claro, proponho isto.\n```json\n{Block}\n```";

        var (text, proposals) = ChatReply.Split(output, Block);

        Assert.Equal("Claro, proponho isto.", text);
        var p = Assert.Single(proposals);
        Assert.Equal("Filtro", p.Title);
        Assert.Null(p.DemandId);
    }

    [Fact]
    public void A_reply_without_proposals_is_kept_as_is()
    {
        var (text, proposals) = ChatReply.Split("  Só uma resposta.  ", null);

        Assert.Equal("Só uma resposta.", text);
        Assert.Empty(proposals);
    }

    [Fact]
    public void Json_that_is_not_the_expected_shape_gives_no_proposals_and_keeps_the_block()
    {
        const string other = "{\"ok\":true}";
        var output = $"Texto\n```json\n{other}\n```";

        var (text, proposals) = ChatReply.Split(output, other);

        Assert.Empty(proposals);
        Assert.Contains("```json", text);
    }

    [Fact]
    public void Proposals_are_capped_titles_trimmed_and_empty_titles_dropped()
    {
        var many = string.Join(",", Enumerable.Range(1, 9).Select(i => $"{{\"title\":\"T{i}\"}}"));
        var json = $"{{\"demands\":[{{\"title\":\"  \"}},{many},{{\"title\":\"{new string('x', 500)}\"}}]}}";

        var parsed = ChatReply.Parse(json);

        Assert.Equal(ChatReply.MaxProposals, parsed.Count);
        Assert.All(parsed, p => Assert.InRange(p.Title.Length, 1, ChatReply.MaxTitle));
        Assert.Equal("T1", parsed[0].Title);
        Assert.Equal("", parsed[0].Description);
    }

    [Theory]
    [InlineData("não é json")]
    [InlineData("[1,2]")]
    [InlineData("{\"demands\":\"x\"}")]
    [InlineData("")]
    public void Garbage_never_throws(string json) => Assert.Empty(ChatReply.Parse(json));
}

/// <summary>
/// Chat com o agente: um "claude" falso (script) responde no lugar do CLI. Confere a retomada da sessão (--resume), o acesso só de leitura
/// ao código, as propostas de demanda, a criação por clique e o painel de agentes ao vivo.
/// </summary>
public sealed class ChatTests(PostgresFixture pg) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-chat-" + Guid.NewGuid().ToString("N"));
    private string _log = "";
    private string? _savedBin;
    private string _origin = "";
    private ApiFactory _factory = null!;
    private HttpClient _http = null!;

    private sealed class Factory(string cs, string temp) : ApiFactory(cs)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Workspaces:Root", Path.Combine(temp, "repos"));
            builder.UseSetting("Git:SshDir", Path.Combine(temp, "ssh"));
            builder.UseSetting("DataProtection:KeysDir", Path.Combine(temp, "keys"));
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        if (OperatingSystem.IsWindows()) return;                              // o "claude" falso é um script sh
        _log = Path.Combine(_temp, "claude.log");
        var script = Path.Combine(_temp, "claude");
        await File.WriteAllTextAsync(script, FakeClaude(_log));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _savedBin = Environment.GetEnvironmentVariable("CLAUDE_BIN");
        Environment.SetEnvironmentVariable("CLAUDE_BIN", script);

        _origin = await GitOrigin.CreateAsync(_temp);
        _factory = new Factory(pg.NewConnectionString(), _temp);
        _http = _factory.CreateClient();
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }

    public Task DisposeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable("CLAUDE_BIN", _savedBin);
            _factory.Dispose();
        }
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    private static string FakeClaude(string log) => $$"""
        #!/bin/sh
        echo "$@" | tr '\n' ' ' >> '{{log}}'; echo >> '{{log}}'
        case "$1" in
          --version) echo "9.9.9"; exit 0;;
          auth) echo '{"loggedIn":true,"authMethod":"teste","email":"t@t"}'; exit 0;;
        esac
        IN=$(cat)
        case "$IN" in
          *LENTO*) sleep 2;;
        esac
        case "$IN" in
          *FALHAR*) echo '{"type":"result","is_error":true,"result":"boom","session_id":"x"}'; exit 1;;
          *LIMITE*) echo '{"type":"result","is_error":true,"result":"You have hit your limit · resets 3pm (UTC)"}'; exit 1;;
        esac
        printf '%s\n' '{"type":"result","is_error":false,"session_id":"sess-1","total_cost_usd":0.01,"result":"Claro! Proponho uma demanda.\n```json\n{\"demands\":[{\"title\":\"Filtro por status\",\"description\":\"Filtrar tarefas por status\"},{\"title\":\"Paginação\",\"description\":\"\"}]}\n```"}'
        """;

    private async Task<Guid> NewProject()
    {
        var res = await _http.PostAsJsonAsync("/api/projects", new { name = "Proj" + Guid.NewGuid().ToString("N")[..4], repositoryUrl = _origin, baseBranch = "main" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> NewChat(Guid project)
    {
        var res = await _http.PostAsJsonAsync("/api/chats", new { projectId = project });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Send(Guid chat, string text) => _http.PostAsJsonAsync($"/api/chats/{chat}/messages", new { text });

    private Task<JsonElement> Detail(Guid chat) => _http.GetFromJsonAsync<JsonElement>($"/api/chats/{chat}");

    private async Task<JsonElement> WaitForAssistant(Guid chat, int count = 1)
    {
        JsonElement last = default;
        Assert.True(await OrchestratorHarness.UntilAsync(async () =>
        {
            last = await Detail(chat);
            return last.GetProperty("messages").EnumerateArray().Count(m => m.GetProperty("role").GetString() == "Assistant") >= count
                   && !last.GetProperty("session").GetProperty("busy").GetBoolean();
        }, 40), "o agente responde");
        return last;
    }

    private static JsonElement LastAssistant(JsonElement detail) =>
        detail.GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "Assistant");

    [PosixFact]
    public async Task The_agent_answers_reading_the_code_read_only_proposes_demands_and_resumes_the_session()
    {
        var project = await NewProject();
        var chat = await NewChat(project);

        var accepted = await Send(chat, "Quero um filtro por status nas tarefas");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var first = LastAssistant(await WaitForAssistant(chat));

        Assert.Contains("Claro! Proponho uma demanda.", first.GetProperty("text").GetString());
        Assert.DoesNotContain("```", first.GetProperty("text").GetString());        // o bloco JSON virou botões
        var proposals = first.GetProperty("proposals").EnumerateArray().ToList();
        Assert.Equal(2, proposals.Count);
        Assert.Equal("Filtro por status", proposals[0].GetProperty("title").GetString());
        Assert.False(first.GetProperty("failed").GetBoolean());

        // 1ª rodada: sem --resume, só leitura do código, com o contexto do projeto no prompt de sistema
        var firstArgs = (await File.ReadAllLinesAsync(_log)).Single(l => l.Contains("--append-system-prompt") && !l.Contains("--resume"));
        Assert.DoesNotContain("--resume", firstArgs);
        Assert.Contains("--allowedTools Read,Glob,Grep", firstArgs);
        Assert.Contains("Proj", firstArgs);
        Assert.DoesNotContain("Edit", firstArgs);

        // 2ª rodada: retoma a sessão do CLI
        await Send(chat, "E a paginação?");
        await WaitForAssistant(chat, count: 2);
        var lines = await File.ReadAllLinesAsync(_log);
        Assert.Contains(lines, l => l.Contains("--resume sess-1"));
        Assert.Equal(4, (await Detail(chat)).GetProperty("messages").GetArrayLength());        // 2 da pessoa + 2 do agente
    }

    [PosixFact]
    public async Task Only_one_message_runs_at_a_time()
    {
        var chat = await NewChat(await NewProject());

        Assert.Equal(HttpStatusCode.Accepted, (await Send(chat, "LENTO primeira")).StatusCode);
        var second = await Send(chat, "segunda");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        await WaitForAssistant(chat);
        Assert.Equal(HttpStatusCode.Accepted, (await Send(chat, "agora sim")).StatusCode);   // liberou
        await WaitForAssistant(chat, count: 2);
    }

    [PosixFact]
    public async Task A_proposal_becomes_a_demand_only_when_the_person_creates_it_and_only_once()
    {
        var project = await NewProject();
        var chat = await NewChat(project);
        await Send(chat, "proponha");
        var reply = LastAssistant(await WaitForAssistant(chat));
        var message = reply.GetProperty("id").GetGuid();
        Assert.Equal(0, await CountDemands(project));                                          // propor não cria nada

        var created = await _http.PostAsJsonAsync($"/api/chats/{chat}/messages/{message}/demands/0", new { });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("started").GetBoolean());
        Assert.Equal("Filtro por status", body.GetProperty("demand").GetProperty("title").GetString());
        Assert.Equal("New", body.GetProperty("demand").GetProperty("status").GetString());
        Assert.Equal(project, body.GetProperty("demand").GetProperty("projectId").GetGuid());
        Assert.Equal(1, await CountDemands(project));

        // a 2ª tentativa não duplica, e a conversa lembra qual demanda nasceu
        var again = await _http.PostAsJsonAsync($"/api/chats/{chat}/messages/{message}/demands/0", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(1, await CountDemands(project));
        var saved = LastAssistant(await Detail(chat)).GetProperty("proposals")[0];
        Assert.Equal(body.GetProperty("demand").GetProperty("id").GetGuid(), saved.GetProperty("demandId").GetGuid());

        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsJsonAsync($"/api/chats/{chat}/messages/{message}/demands/9", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsJsonAsync($"/api/chats/{chat}/messages/{Guid.NewGuid()}/demands/0", new { })).StatusCode);
    }

    [PosixFact]
    public async Task Create_and_start_moves_the_demand_to_refinement()
    {
        var project = await NewProject();
        var chat = await NewChat(project);
        await Send(chat, "proponha");
        var message = LastAssistant(await WaitForAssistant(chat)).GetProperty("id").GetGuid();

        var created = await _http.PostAsJsonAsync($"/api/chats/{chat}/messages/{message}/demands/1", new { start = true });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.True((await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("started").GetBoolean());
    }

    [PosixFact]
    public async Task Failures_and_usage_limits_are_shown_in_the_conversation_without_breaking_it()
    {
        var chat = await NewChat(await NewProject());

        await Send(chat, "FALHAR por favor");
        var failed = LastAssistant(await WaitForAssistant(chat));
        Assert.True(failed.GetProperty("failed").GetBoolean());
        Assert.Contains("boom", failed.GetProperty("text").GetString());

        await Send(chat, "LIMITE");
        var limited = LastAssistant(await WaitForAssistant(chat, count: 2));
        Assert.True(limited.GetProperty("failed").GetBoolean());
        Assert.Contains("sem uso disponível", limited.GetProperty("text").GetString());

        await Send(chat, "voltou");                                                            // a conversa continua funcionando
        // o limite fica registrado: a execução nem inicia; a resposta é a mesma explicação
        var again = LastAssistant(await WaitForAssistant(chat, count: 3));
        Assert.True(again.GetProperty("failed").GetBoolean());
    }

    [PosixFact]
    public async Task Validation_listing_and_deleting()
    {
        var project = await NewProject();

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/chats", new { projectId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/chats", new { projectId = project, adapterName = "nao-existe" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/chats", new { projectId = project, environmentId = Guid.NewGuid() })).StatusCode);

        var chat = await NewChat(project);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(chat, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(chat, new string('x', ChatService.MaxMessageChars + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(Guid.NewGuid(), "oi")).StatusCode);

        await Send(chat, "Primeira pergunta sobre o projeto");
        await WaitForAssistant(chat);
        var list = await _http.GetFromJsonAsync<JsonElement>("/api/chats");
        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal("Primeira pergunta sobre o projeto", item.GetProperty("title").GetString());   // o título vem da 1ª mensagem
        Assert.Equal("Padrão do servidor", item.GetProperty("environment").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/chats/{chat}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/chats/{chat}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/chats")).StatusCode);
    }

    [PosixFact]
    public async Task The_live_panel_lists_every_machines_agents_the_unreachable_ones_and_what_is_running()
    {
        var project = await NewProject();
        var down = await _http.PostAsJsonAsync("/api/environments",
            new { name = "Fora do ar", url = "http://127.0.0.1:1", token = "t".PadRight(30, 't'), workspaceRoot = "/x" });
        Assert.Equal(HttpStatusCode.Created, down.StatusCode);
        Guid runId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var demand = new Demand { ProjectId = project, Title = "Demanda viva", Status = DemandStatus.Refining };
            var item = new WorkItem { DemandId = demand.Id, Title = "Tarefa viva", State = WorkItemState.InDevelopment };
            var run = new AgentRun { DemandId = demand.Id, WorkItemId = item.Id, Role = AgentRole.Dev, Prompt = "p", Status = AgentRunStatus.Running };
            db.AddRange(demand, item, run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        var live = await _http.GetFromJsonAsync<JsonElement>("/api/live/agents");

        var agents = live.GetProperty("agents").EnumerateArray().ToList();
        var local = agents.Single(a => a.GetProperty("environment").GetString() == "Padrão do servidor");
        Assert.True(local.GetProperty("reachable").GetBoolean());
        Assert.True(local.GetProperty("installed").GetBoolean());
        Assert.True(local.GetProperty("authenticated").GetBoolean());
        Assert.Equal("9.9.9", local.GetProperty("version").GetString());
        Assert.Equal(1, local.GetProperty("runningNow").GetInt32());

        var remote = agents.Single(a => a.GetProperty("environment").GetString() == "Fora do ar");
        Assert.False(remote.GetProperty("reachable").GetBoolean());
        Assert.False(remote.GetProperty("authenticated").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(remote.GetProperty("detail").GetString()));

        var running = Assert.Single(live.GetProperty("running").EnumerateArray());
        Assert.Equal(runId, running.GetProperty("runId").GetGuid());
        Assert.Equal("Dev", running.GetProperty("role").GetString());
        Assert.Equal("Tarefa viva", running.GetProperty("task").GetString());
        Assert.Equal(project, running.GetProperty("projectId").GetGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/live/agents")).StatusCode);
    }

    private async Task<int> CountDemands(Guid project)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Demands.CountAsync(d => d.ProjectId == project);
    }
}
