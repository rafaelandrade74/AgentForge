using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentForge.Api.GitHub;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Workflow;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests;

/// <summary>
/// Pull request da tarefa concluída: o servidor publica SÓ o branch da tarefa e abre o PR no GitHub (simulado) para a branch de destino
/// do projeto. O "GitHub" do git é um repositório bare local: um `insteadOf` redireciona https://github.com/ (e a URL com o token) para ele.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PullRequestTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-pr-" + Guid.NewGuid().ToString("N"));
    private string Remote => Path.Combine(_temp, "gitserver", "octo", "privado.git");
    private readonly string?[] _savedEnv = new string?[7];
    private static readonly string[] EnvNames = ["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1", "GIT_CONFIG_KEY_2", "GIT_CONFIG_VALUE_2"];
    private FakeGitHub _github = null!;
    private Factory _factory = null!;
    private HttpClient _http = null!;

    private sealed class Factory(string cs, string temp, FakeGitHub github) : ApiFactory(cs)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Workspaces:Root", Path.Combine(temp, "repos"));
            builder.UseSetting("Git:SshDir", Path.Combine(temp, "ssh"));
            builder.UseSetting("DataProtection:KeysDir", Path.Combine(temp, "keys"));
            builder.ConfigureServices(s => s.AddHttpClient<GitHubClient>().ConfigurePrimaryHttpMessageHandler(() => github));
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temp);
        var origin = await GitOrigin.CreateAsync(_temp);
        Directory.CreateDirectory(Path.GetDirectoryName(Remote)!);
        await GitCli.RunAsync(_temp, ["clone", "--bare", origin, Remote], default);

        for (var i = 0; i < EnvNames.Length; i++) _savedEnv[i] = Environment.GetEnvironmentVariable(EnvNames[i]);
        var server = "file://" + Path.Combine(_temp, "gitserver").Replace('\\', '/') + "/";
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "3");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", $"url.{server}.insteadOf");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "https://github.com/");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_1", $"url.{server}.insteadOf");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_1", $"https://x-access-token:{FakeGitHub.Token}@github.com/");
        // repositório por SSH (git@github.com:...) também cai no "GitHub" local
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_2", $"url.{server}.insteadOf");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_2", "git@github.com:");

        _github = new FakeGitHub(origin);
        _factory = new Factory(pg.NewConnectionString(), _temp, _github);
        _http = _factory.CreateClient();
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }

    public Task DisposeAsync()
    {
        for (var i = 0; i < EnvNames.Length; i++) Environment.SetEnvironmentVariable(EnvNames[i], _savedEnv[i]);
        _factory.Dispose();
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
        return Task.CompletedTask;
    }

    private async Task<JsonElement> CreateProject(object? extra = null, string url = "https://github.com/octo/privado.git")
    {
        var body = new Dictionary<string, object?> { ["name"] = "P" + Guid.NewGuid().ToString("N")[..5], ["repositoryUrl"] = url, ["baseBranch"] = "main" };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var response = await _http.PostAsJsonAsync("/api/projects", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Uma tarefa que já passou por tudo: branch com um commit de verdade no clone do projeto, worktree removido, estado Done.</summary>
    private async Task<Guid> DoneTask(Guid projectId, WorkItemState state = WorkItemState.Done)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var workspaces = scope.ServiceProvider.GetRequiredService<IWorkspaceManager>();
        var project = await db.Projects.FirstAsync(p => p.Id == projectId);
        var demand = new Demand { ProjectId = projectId, Title = "D", Description = "d", Status = DemandStatus.InProgress };
        var item = new WorkItem { DemandId = demand.Id, Title = "Criar API de tarefas", Description = "Descrição", AcceptanceCriteria = "- existe", State = state };
        db.AddRange(demand, item);
        await db.SaveChangesAsync();

        await workspaces.EnsureDemandBranchAsync(project, demand.Id, default);
        var ws = await workspaces.CreateWorktreeAsync(project, item.Id, demand.Id, default);
        await File.WriteAllTextAsync(Path.Combine(ws.Path, "feature.txt"), "feito\n");
        await GitCli.RunAsync(ws.Path, ["add", "."], default);
        await GitCli.RunAsync(ws.Path, ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-m", "feature"], default);
        await workspaces.RemoveWorktreeAsync(project, item.Id, deleteBranch: false, default);
        item.BranchName = ws.Branch;
        await db.SaveChangesAsync();
        return item.Id;
    }

    /// <summary>Tarefa Done só no banco (sem clone): para os casos que a API recusa antes de chegar ao git.</summary>
    private async Task<Guid> BareDoneTask(Guid projectId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var demand = new Demand { ProjectId = projectId, Title = "D", Status = DemandStatus.InProgress };
        var item = new WorkItem { DemandId = demand.Id, Title = "T", State = WorkItemState.Done };
        item.BranchName = WorkspaceManager.BranchName(item.Id);
        db.AddRange(demand, item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> OpenPr(Guid taskId, object? body = null)
    {
        var response = await _http.PostAsJsonAsync($"/api/work-items/{taskId}/pull-request", body ?? new { });
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private static Guid Id(JsonElement project) => project.GetProperty("id").GetGuid();

    private int Pulls => _github.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/pulls"));

    // ------------------------------------------------------------------ publicação automática ao concluir

    private async Task PublishAsync(Guid task)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IBranchPublisher>().PublishAsync(task, default);
    }

    private async Task<JsonElement> Item(Guid task) => await _http.GetFromJsonAsync<JsonElement>($"/api/work-items/{task}");

    [Fact]
    public async Task A_finished_task_has_its_branch_published_automatically_without_opening_a_pr_or_touching_the_base()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token });
        var task = await DoneTask(Id(project));
        var mainBefore = await GitOrigin.HeadAsync(Remote);

        await PublishAsync(task);

        var branch = $"agentforge/task-{task}";
        Assert.True(await GitOrigin.HasBranchAsync(Remote, branch));
        Assert.Contains("feature", await GitCli.RunAsync(Remote, ["log", "-1", "--format=%s", branch], default));
        Assert.Equal(mainBefore, await GitOrigin.HeadAsync(Remote));       // a base não muda
        Assert.Equal(0, Pulls);                                              // o PR continua sendo ação humana
        var item = await Item(task);
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("pushedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pushError").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pullRequestUrl").ValueKind);

        // publicar de novo não repete o push nem muda nada
        var firstPush = item.GetProperty("pushedAt").GetDateTime();
        await PublishAsync(task);
        Assert.Equal(firstPush, (await Item(task)).GetProperty("pushedAt").GetDateTime());

        // e abrir o PR depois funciona, sem erro (o branch já está lá)
        var (status, _) = await OpenPr(task);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, Pulls);
    }

    [Fact]
    public async Task An_ssh_project_pushes_to_its_ssh_url_with_the_same_key_as_the_pull_and_needs_no_token()
    {
        var project = await CreateProject(url: "git@github.com:octo/privado.git");        // sem token do GitHub
        var task = await DoneTask(Id(project));
        var mainBefore = await GitOrigin.HeadAsync(Remote);

        await PublishAsync(task);                                            // automático ao concluir

        var branch = $"agentforge/task-{task}";
        Assert.True(await GitOrigin.HasBranchAsync(Remote, branch), "pushError: " + (await Item(task)).GetProperty("pushError"));
        Assert.Equal(mainBefore, await GitOrigin.HeadAsync(Remote));
        var item = await Item(task);
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("pushedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pushError").ValueKind);

        // o botão também funciona sem token, mas abrir o PR (API do GitHub) continua exigindo o token
        Assert.Equal(HttpStatusCode.OK, (await _http.PostAsync($"/api/work-items/{task}/push", null)).StatusCode);
        var (status, body) = await OpenPr(task);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("token", body.GetProperty("error").GetString());
        Assert.Equal(0, Pulls);
    }

    [Fact]
    public async Task The_push_button_endpoint_publishes_a_done_task_and_recovers_from_an_earlier_failure()
    {
        var project = await CreateProject();                                 // sem token: a publicação automática registrou o erro
        var task = await DoneTask(Id(project));
        await PublishAsync(task);
        Assert.Contains("token do GitHub", (await Item(task)).GetProperty("pushError").GetString());

        // sem token a ação manual também recusa, com o motivo
        var refused = await _http.PostAsync($"/api/work-items/{task}/push", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // cadastra o token e usa o botão
        var edit = await _http.PutAsJsonAsync($"/api/projects/{Id(project)}", new
        {
            name = project.GetProperty("name").GetString(), repositoryUrl = "https://github.com/octo/privado.git", baseBranch = "main",
            githubToken = FakeGitHub.Token,
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var mainBefore = await GitOrigin.HeadAsync(Remote);

        var pushed = await _http.PostAsync($"/api/work-items/{task}/push", null);

        Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
        var body = await pushed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"agentforge/task-{task}", body.GetProperty("branch").GetString());
        Assert.True(await GitOrigin.HasBranchAsync(Remote, $"agentforge/task-{task}"));
        Assert.Equal(mainBefore, await GitOrigin.HeadAsync(Remote));
        Assert.Equal(0, Pulls);
        var item = await Item(task);
        Assert.NotEqual(JsonValueKind.Null, item.GetProperty("pushedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pushError").ValueKind);
        Assert.DoesNotContain(FakeGitHub.Token, body.GetRawText());
    }

    [Fact]
    public async Task The_push_button_endpoint_only_accepts_done_tasks_and_known_ids()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token });
        var waiting = await DoneTask(Id(project), WorkItemState.AwaitingApproval);

        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsync($"/api/work-items/{waiting}/push", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync($"/api/work-items/{Guid.NewGuid()}/push", null)).StatusCode);
        Assert.False(await GitOrigin.HasBranchAsync(Remote, $"agentforge/task-{waiting}"));
    }

    [Fact]
    public async Task Without_a_github_token_the_branch_is_not_pushed_and_the_task_says_why()
    {
        var project = await CreateProject();                                 // sem token
        var task = await DoneTask(Id(project));

        await PublishAsync(task);

        Assert.False(await GitOrigin.HasBranchAsync(Remote, $"agentforge/task-{task}"));
        var item = await Item(task);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pushedAt").ValueKind);
        Assert.Contains("token do GitHub", item.GetProperty("pushError").GetString());
        Assert.Equal("Done", item.GetProperty("state").GetString());          // continua concluída
    }

    [Fact]
    public async Task A_failed_automatic_push_is_recorded_without_the_token_and_never_throws()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token });
        var task = await BareDoneTask(Id(project));                          // Done, mas sem clone/branch na máquina: o push falha

        await PublishAsync(task);                                            // não lança

        var item = await Item(task);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pushedAt").ValueKind);
        var error = item.GetProperty("pushError").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain(FakeGitHub.Token, error);
    }

    [Fact]
    public async Task Only_a_done_task_is_ever_published()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token });
        var task = await DoneTask(Id(project), WorkItemState.AwaitingApproval);

        await PublishAsync(task);

        Assert.False(await GitOrigin.HasBranchAsync(Remote, $"agentforge/task-{task}"));
        Assert.Equal(JsonValueKind.Null, (await Item(task)).GetProperty("pushedAt").ValueKind);
    }

    // ------------------------------------------------------------------ fluxo feliz

    [Fact]
    public async Task Pushes_only_the_task_branch_and_opens_the_pr_to_the_projects_target_branch()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token, pullRequestBaseBranch = "develop" });
        var task = await DoneTask(Id(project));
        var mainBefore = await GitOrigin.HeadAsync(Remote);

        var (status, pr) = await OpenPr(task);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("https://github.com/octo/privado/pull/7", pr.GetProperty("url").GetString());
        Assert.Equal(7, pr.GetProperty("number").GetInt32());
        Assert.Equal("develop", pr.GetProperty("baseBranch").GetString());
        Assert.False(pr.GetProperty("alreadyExisted").GetBoolean());

        // o branch da tarefa chegou ao "GitHub" com o commit; a base não mudou
        var branch = $"agentforge/task-{task}";
        Assert.True(await GitOrigin.HasBranchAsync(Remote, branch));
        Assert.Contains("feature", await GitCli.RunAsync(Remote, ["log", "-1", "--format=%s", branch], default));
        Assert.Equal(mainBefore, await GitOrigin.HeadAsync(Remote));
        Assert.False(await GitOrigin.HasBranchAsync(Remote, "develop"));

        // o PR foi pedido ao GitHub com o token do projeto, head = branch da tarefa, base = develop
        var call = _github.Requests.Single(r => r.Method == "POST" && r.Path.EndsWith("/pulls"));
        Assert.Equal("repos/octo/privado/pulls", call.Path);
        Assert.Equal($"Bearer {FakeGitHub.Token}", call.Authorization);
        using var sent = JsonDocument.Parse(call.Body);
        Assert.Equal(branch, sent.RootElement.GetProperty("head").GetString());
        Assert.Equal("develop", sent.RootElement.GetProperty("base").GetString());
        Assert.Equal("Criar API de tarefas", sent.RootElement.GetProperty("title").GetString());
        Assert.Contains("existe", sent.RootElement.GetProperty("body").GetString());

        // fica registrado na tarefa e a 2ª chamada não repete nada
        var item = await _http.GetFromJsonAsync<JsonElement>($"/api/work-items/{task}");
        Assert.Equal(7, item.GetProperty("pullRequestNumber").GetInt32());
        var (again, second) = await OpenPr(task);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.True(second.GetProperty("alreadyExisted").GetBoolean());
        Assert.Equal(1, Pulls);
    }

    [Fact]
    public async Task The_request_can_override_the_target_and_without_settings_the_base_branch_is_used()
    {
        var withTarget = await CreateProject(new { githubToken = FakeGitHub.Token, pullRequestBaseBranch = "develop" });
        var plain = await CreateProject(new { githubToken = FakeGitHub.Token });

        var (_, overridden) = await OpenPr(await DoneTask(Id(withTarget)), new { baseBranch = "release/1.0" });
        var (_, fallback) = await OpenPr(await DoneTask(Id(plain)));

        Assert.Equal("release/1.0", overridden.GetProperty("baseBranch").GetString());
        Assert.Equal("main", fallback.GetProperty("baseBranch").GetString());
    }

    [Fact]
    public async Task An_existing_open_pr_for_the_branch_is_returned_instead_of_failing()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token });
        _github.PullAlreadyExists = true;

        var (status, pr) = await OpenPr(await DoneTask(Id(project)));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, pr.GetProperty("number").GetInt32());
        Assert.True(pr.GetProperty("alreadyExisted").GetBoolean());
    }

    // ------------------------------------------------------------------ recusas

    [Fact]
    public async Task Only_finished_tasks_with_a_token_on_a_github_project_can_open_a_pr()
    {
        var withToken = await CreateProject(new { githubToken = FakeGitHub.Token });
        var withoutToken = await CreateProject();
        var notGithub = await CreateProject(new { githubToken = FakeGitHub.Token }, url: "https://gitlab.com/octo/privado.git");

        var notDone = await OpenPr(await DoneTask(Id(withToken), WorkItemState.AwaitingApproval));
        var noToken = await OpenPr(await BareDoneTask(Id(withoutToken)));
        var gitlab = await OpenPr(await BareDoneTask(Id(notGithub)));
        var ghost = await _http.PostAsJsonAsync($"/api/work-items/{Guid.NewGuid()}/pull-request", new { });
        var badBranch = await OpenPr(await DoneTask(Id(withToken)), new { baseBranch = "--delete" });

        Assert.Equal(HttpStatusCode.Conflict, notDone.Status);
        Assert.Contains("Done", notDone.Body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, noToken.Status);
        Assert.Contains("token do GitHub", noToken.Body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, gitlab.Status);
        Assert.Contains("GitHub", gitlab.Body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, ghost.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, badBranch.Status);
        Assert.Equal(0, Pulls);                                   // nenhuma chamada ao GitHub, nenhum push
        Assert.False(await GitOrigin.HasBranchAsync(Remote, "agentforge/*"));
    }

    [Fact]
    public async Task When_github_refuses_the_pr_the_branch_stays_published_and_the_reason_is_shown()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token, pullRequestBaseBranch = "nao-existe" });
        var task = await DoneTask(Id(project));
        _github.PullRejected = true;

        var (status, body) = await OpenPr(task);

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("branch foi publicado", body.GetProperty("error").GetString());
        Assert.Contains("base invalid", body.GetProperty("error").GetString());
        Assert.True(await GitOrigin.HasBranchAsync(Remote, $"agentforge/task-{task}"));
        var item = await _http.GetFromJsonAsync<JsonElement>($"/api/work-items/{task}");
        Assert.Equal(JsonValueKind.Null, item.GetProperty("pullRequestUrl").ValueKind);   // sem PR registrado: dá para tentar de novo

        _github.PullRejected = false;
        Assert.Equal(HttpStatusCode.OK, (await OpenPr(task)).Status);
    }

    [Fact]
    public async Task A_failed_push_never_leaks_the_token()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token });
        var task = await DoneTask(Id(project));
        // o branch some do clone: o push falha
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repo = Path.Combine(_temp, "repos", Id(project).ToString(), "repo");
            await GitCli.RunAsync(repo, ["branch", "-D", $"agentforge/task-{task}"], default);
        }

        var (status, body) = await OpenPr(task);

        Assert.NotEqual(HttpStatusCode.OK, status);
        Assert.DoesNotContain(FakeGitHub.Token, body.GetRawText());
        Assert.Equal(0, Pulls);
    }

    // ------------------------------------------------------------------ configuração do projeto

    [Fact]
    public async Task The_token_is_stored_protected_never_returned_and_can_be_kept_replaced_or_cleared()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token, pullRequestBaseBranch = "develop" });
        var id = Id(project);
        Assert.True(project.GetProperty("hasGitHubToken").GetBoolean());
        Assert.Equal("develop", project.GetProperty("pullRequestBaseBranch").GetString());
        Assert.DoesNotContain(FakeGitHub.Token, project.GetRawText());

        var stored = await _factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Projects.AsNoTracking().FirstAsync(p => p.Id == id);
        Assert.NotEmpty(stored.ProtectedGitHubToken);
        Assert.DoesNotContain(FakeGitHub.Token, stored.ProtectedGitHubToken);

        // editar sem informar o token mantém; "" apaga; um novo substitui
        var edit = new Dictionary<string, object?> { ["name"] = "P", ["repositoryUrl"] = "https://github.com/octo/privado.git", ["baseBranch"] = "main" };
        var kept = await (await _http.PutAsJsonAsync($"/api/projects/{id}", edit)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(kept.GetProperty("hasGitHubToken").GetBoolean());
        Assert.Equal("develop", kept.GetProperty("pullRequestBaseBranch").GetString());

        edit["githubToken"] = "";
        var cleared = await (await _http.PutAsJsonAsync($"/api/projects/{id}", edit)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(cleared.GetProperty("hasGitHubToken").GetBoolean());

        edit["githubToken"] = FakeGitHub.Token;
        edit["pullRequestBaseBranch"] = "";
        var replaced = await (await _http.PutAsJsonAsync($"/api/projects/{id}", edit)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(replaced.GetProperty("hasGitHubToken").GetBoolean());
        Assert.Equal("", replaced.GetProperty("pullRequestBaseBranch").GetString());
    }

    [Fact]
    public async Task Invalid_tokens_and_branch_names_are_rejected()
    {
        foreach (var bad in new object[]
        {
            new { githubToken = "curto" }, new { githubToken = "tem espaco no meio do token 123456789" }, new { githubToken = "tem@arroba/e-barra-123456789012345" },
            new { pullRequestBaseBranch = "--delete" }, new { pullRequestBaseBranch = "a..b" }, new { pullRequestBaseBranch = "com espaco" },
        })
        {
            var body = new Dictionary<string, object?> { ["name"] = "X", ["repositoryUrl"] = "https://github.com/octo/privado.git" };
            foreach (var p in bad.GetType().GetProperties()) body[p.Name] = p.GetValue(bad);
            Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/projects", body)).StatusCode);
        }
    }

    // ------------------------------------------------------------------ MCP

    private async Task<(bool IsError, string Text)> Tool(HttpClient mcp, string name, object arguments)
    {
        var response = await mcp.PostAsync("/mcp", new StringContent(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } }), Encoding.UTF8, "application/json"));
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        return (result.GetProperty("isError").GetBoolean(), result.GetProperty("content")[0].GetProperty("text").GetString()!);
    }

    private async Task<HttpClient> McpClient()
    {
        var token = (await (await _http.PostAsJsonAsync("/api/tokens", new { name = "pr" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var mcp = _factory.CreateClient();
        mcp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return mcp;
    }

    [Fact]
    public async Task Through_mcp_the_target_branch_can_be_changed_and_the_pr_opened_but_the_token_cannot_be_set()
    {
        var mcp = await McpClient();
        var project = await CreateProject(new { githubToken = FakeGitHub.Token });
        var id = Id(project);

        var updated = await Tool(mcp, "update_project", new { projectId = id, pullRequestBaseBranch = "develop", autoMode = true });
        Assert.False(updated.IsError, updated.Text);
        var dto = JsonDocument.Parse(updated.Text).RootElement;
        Assert.Equal("develop", dto.GetProperty("pullRequestBaseBranch").GetString());
        Assert.True(dto.GetProperty("autoMode").GetBoolean());
        Assert.Equal("main", dto.GetProperty("baseBranch").GetString());                 // o que não veio não mudou
        Assert.True(dto.GetProperty("hasGitHubToken").GetBoolean());
        Assert.DoesNotContain(FakeGitHub.Token, updated.Text);
        Assert.True((await Tool(mcp, "update_project", new { projectId = id, pullRequestBaseBranch = "--x" })).IsError);

        var task = await DoneTask(id);
        var pending = await Tool(mcp, "list_pending_actions", new { });
        Assert.Contains("open_pull_request", pending.Text);
        Assert.Contains(task.ToString(), pending.Text);
        Assert.Contains("develop", pending.Text);

        var opened = await Tool(mcp, "open_pull_request", new { taskId = task });
        Assert.False(opened.IsError, opened.Text);
        Assert.Equal("develop", JsonDocument.Parse(opened.Text).RootElement.GetProperty("baseBranch").GetString());
        Assert.Contains("pull/7", opened.Text);
        Assert.DoesNotContain(task.ToString(), (await Tool(mcp, "list_pending_actions", new { })).Text.Split("readyForPullRequest")[1].Split("demandsNotStarted")[0]);

        var early = await Tool(mcp, "open_pull_request", new { taskId = await DoneTask(id, WorkItemState.Qa) });
        Assert.True(early.IsError);
        Assert.Contains("Done", early.Text);
    }

    // ------------------------------------------------------------------ git: o push é só do branch da tarefa

    [Fact]
    public async Task Pushing_rejects_unsafe_urls_and_hides_the_credential_in_errors()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token });
        var task = await DoneTask(Id(project));
        await using var scope = _factory.Services.CreateAsyncScope();
        var workspaces = scope.ServiceProvider.GetRequiredService<IWorkspaceManager>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var p = await db.Projects.FirstAsync(x => x.Id == Id(project));

        foreach (var bad in new[] { "http://x/y.git", "git@github.com:o/r .git", "-oProxyCommand=x@h:y", "ext::sh -c x", "https://a b/c.git", "--receive-pack=x", "/tmp/x" })
            await Assert.ThrowsAsync<WorkspaceException>(() => workspaces.PushTaskBranchAsync(p, task, bad, default));

        var ex = await Assert.ThrowsAsync<GitException>(() =>
            workspaces.PushTaskBranchAsync(p, task, "https://x-access-token:SEGREDO123456789012345@127.0.0.1:1/octo/x.git", default));
        Assert.DoesNotContain("SEGREDO123456789012345", ex.Message);
    }

    [Fact]
    public async Task IsBranchPublished_compara_o_sha_local_com_o_remoto_e_falha_sem_vazar()
    {
        var project = await CreateProject(new { githubToken = FakeGitHub.Token });
        var task = await DoneTask(Id(project));
        await using var scope = _factory.Services.CreateAsyncScope();
        var workspaces = scope.ServiceProvider.GetRequiredService<IWorkspaceManager>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var p = await db.Projects.FirstAsync(x => x.Id == Id(project));

        var remote = Path.Combine(_temp, "remoto-" + Guid.NewGuid().ToString("N")[..6] + ".git");
        Directory.CreateDirectory(remote);
        await GitCli.RunAsync(remote, ["init", "--bare"], default);
        var url = "file://" + remote;

        Assert.False(await workspaces.IsBranchPublishedAsync(p, task, url, default));   // ausente no remoto
        await workspaces.PushTaskBranchAsync(p, task, url, default);
        Assert.True(await workspaces.IsBranchPublishedAsync(p, task, url, default));    // mesmo commit
        Assert.False(await workspaces.IsBranchPublishedAsync(p, task, "https://x-access-token:SEGREDO123456789012345@127.0.0.1:1/o/x.git", default)); // falha → false
        Assert.False(await workspaces.IsBranchPublishedAsync(p, task, "--receive-pack=x", default));                 // URL insegura
        Assert.False(await workspaces.IsBranchPublishedAsync(p, Guid.NewGuid(), url, default));                       // sem branch local
    }
}
