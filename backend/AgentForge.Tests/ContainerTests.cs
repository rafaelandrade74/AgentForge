using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Environments;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentForge.Tests;

/// <summary>
/// Perfis de stack e container por tarefa (fase 12). O host agent é de verdade (em processo); o "docker" é um script que
/// registra os argumentos recebidos, então o que se verifica é o que o AgentForge pede ao Docker, sem precisar de Docker.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContainerTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Token = "token-do-host-agent-com-mais-de-24-caracteres";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "af-ctr-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_temp, "root");
    private string DockerLog => Path.Combine(_temp, "docker.log");
    private string BuiltMarker => Path.Combine(_temp, "built");
    private string Received => Path.Combine(_temp, "Dockerfile.received");
    private string ProjectDir(Guid id) => $"{Root}/{id}";
    private WebApplication? _agent;
    private WebApplication? _plain;
    private Factory _factory = null!;
    private HttpClient _http = null!;
    private RemoteProcessLauncher _remote = null!;
    private readonly string _cs = pg.NewConnectionString();

    private sealed class Factory(string cs, string temp) : ApiFactory(cs)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Workspaces:Root", Path.Combine(temp, "default"));
            builder.UseSetting("DataProtection:KeysDir", Path.Combine(temp, "keys"));
        }
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        if (OperatingSystem.IsWindows()) return;

        var script = Path.Combine(_temp, "docker");
        await File.WriteAllTextAsync(script, $$"""
            #!/bin/sh
            echo "ARGS $*" >> '{{DockerLog}}'
            case "$1" in
              run) echo 'result:{"ok":true}' ;;
              image) [ -f '{{BuiltMarker}}' ] && exit 0 || exit 1 ;;
              build) cat > '{{Received}}'; touch '{{BuiltMarker}}'; echo 'Step 1/1 : FROM x' ;;
              rm) ;;
            esac
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _agent = AgentForge.Tests.Orchestrator.HostAgentFactory.Create(Root, Token,
            docker: (script, Path.Combine(_temp, "claude-config"), ["minha/aprovada:*"]));
        _plain = AgentForge.Tests.Orchestrator.HostAgentFactory.Create(Root, Token);   // Docker desligado (padrão)
        await _agent.StartAsync();
        await _plain.StartAsync();
        _remote = new RemoteProcessLauncher(new HttpClient(), _agent.Urls.First(), Token);

        _factory = new Factory(_cs, _temp);
        _http = _factory.CreateClient();
        var login = await _http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        if (_agent is not null) await _agent.DisposeAsync();
        if (_plain is not null) await _plain.DisposeAsync();
        foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_temp, recursive: true);
    }

    private string Log() => File.Exists(DockerLog) ? File.ReadAllText(DockerLog) : "";

    private static ContainerSpec Spec(string mount, string image = "agentforge/stack:abc123") => new(image, null, "bridge", 2, 1024, mount);

    private async Task<string> RunInContainer(ContainerSpec spec, string workingDirectory)
    {
        await using var process = await _remote.StartAsync(new LaunchSpec("claude", ["-p", "--model", "sonnet"], workingDirectory, "oi", Container: spec), default);
        var lines = new List<string>();
        string? line;
        while ((line = await process.ReadStdoutLineAsync(default)) is not null) lines.Add(line);
        await process.WaitForExitAsync(default);
        return string.Join('\n', lines);
    }

    // ------------------------------------------------------------------ host agent

    [PosixFact]
    public async Task The_host_agent_runs_the_agent_in_a_locked_down_container_with_only_the_project_folder()
    {
        var project = Guid.NewGuid();
        var work = $"{ProjectDir(project)}/worktrees/t1";
        Directory.CreateDirectory(work);

        var output = await RunInContainer(Spec(ProjectDir(project)), work);

        Assert.Equal("""result:{"ok":true}""", output);
        var run = Log().Split('\n').Single(l => l.StartsWith("ARGS run "));
        Assert.Contains("--cap-drop ALL", run);
        Assert.Contains("--security-opt no-new-privileges", run);
        Assert.Contains($"-v {ProjectDir(project)}:{ProjectDir(project)}", run);
        Assert.Contains("-v " + Path.Combine(_temp, "claude-config") + ":/agent-config", run);
        Assert.Contains($"-w {work}", run);
        Assert.Contains("agentforge/stack:abc123 claude -p --model sonnet", run);
        Assert.DoesNotContain("--privileged", run);
        Assert.DoesNotContain("docker.sock", run);
        Assert.Equal(2, run.Split(' ').Count(a => a == "-v"));
    }

    [PosixFact]
    public async Task The_container_is_removed_when_the_process_ends_so_nothing_is_left_running()
    {
        var project = Guid.NewGuid();
        Directory.CreateDirectory($"{ProjectDir(project)}/worktrees/t1");

        await RunInContainer(Spec(ProjectDir(project)), $"{ProjectDir(project)}/worktrees/t1");

        await WaitFor(() => Log().Contains("ARGS rm -f af-"));
    }

    [PosixFact]
    public async Task Requests_that_widen_the_sandbox_are_refused()
    {
        var project = Guid.NewGuid();
        var work = $"{ProjectDir(project)}/worktrees/t1";
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(_temp, "fora"));

        async Task<string> Refused(ContainerSpec spec, string wd) =>
            (await Assert.ThrowsAsync<ProcessStartException>(() => RunInContainer(spec, wd))).Message;

        Assert.Contains("não permitida", await Refused(Spec(ProjectDir(project), "alpine:3"), work));                       // imagem fora da lista
        Assert.Contains("não permitida", await Refused(Spec(ProjectDir(project), "--privileged"), work));                   // vira opção do docker
        Assert.Contains("fora das pastas permitidas", await Refused(Spec(Path.Combine(_temp, "fora")), Path.Combine(_temp, "fora")));
        Assert.Contains("fora das pastas permitidas", await Refused(Spec("/"), work));                                       // montar a raiz
        Assert.Contains("fora das pastas permitidas", await Refused(Spec(Root), work));                                      // a raiz de todos os projetos
        Assert.Contains("Rede inválida", await Refused(Spec(ProjectDir(project)) with { Network = "host" }, work));
        Assert.Contains("dentro da pasta montada", await Refused(Spec(ProjectDir(project)), Root));
        Assert.DoesNotContain("ARGS run", Log());                                                                           // nada chegou ao docker
    }

    [PosixFact]
    public async Task Images_from_the_hosts_allow_list_are_accepted()
    {
        var project = Guid.NewGuid();
        Directory.CreateDirectory($"{ProjectDir(project)}/worktrees/t1");

        await RunInContainer(Spec(ProjectDir(project), "minha/aprovada:1"), $"{ProjectDir(project)}/worktrees/t1");

        Assert.Contains("minha/aprovada:1 claude", Log());
    }

    [PosixFact]
    public async Task A_host_agent_without_docker_enabled_refuses_containers()
    {
        var plain = new RemoteProcessLauncher(new HttpClient(), _plain!.Urls.First(), Token);
        var project = Guid.NewGuid();
        Directory.CreateDirectory($"{ProjectDir(project)}/worktrees/t1");

        var ex = await Assert.ThrowsAsync<ProcessStartException>(() => plain.StartAsync(
            new LaunchSpec("claude", [], $"{ProjectDir(project)}/worktrees/t1", Container: Spec(ProjectDir(project))), default));

        Assert.Contains("não estão habilitados", ex.Message);
        Assert.False((await plain.InfoAsync(default)).ContainersEnabled);
        Assert.True((await _remote.InfoAsync(default)).ContainersEnabled);
    }

    [PosixFact]
    public async Task The_image_is_built_once_from_the_dockerfile_and_rebuilt_only_when_forced()
    {
        var dockerfile = "FROM node:22\nRUN echo oi\n";
        var tag = ContainerRules.ImageTag(dockerfile);

        var first = await _remote.EnsureImageAsync(tag, dockerfile, force: false, default);
        var second = await _remote.EnsureImageAsync(tag, dockerfile, force: false, default);
        var forced = await _remote.EnsureImageAsync(tag, dockerfile, force: true, default);

        Assert.True(first.Built);
        Assert.Contains("Step 1/1", first.Log);                      // o log da construção fica visível
        Assert.Equal(dockerfile, await File.ReadAllTextAsync(Received));   // o Dockerfile vai pelo stdin, sem contexto de arquivos
        Assert.False(second.Built);
        Assert.True(second.Present);
        Assert.True(forced.Built);
        Assert.DoesNotContain(Log().Split('\n'), l => l.StartsWith("ARGS build") && !l.EndsWith(" -t " + tag + " -") && !l.Contains("-t " + tag + " -"));
    }

    [PosixFact]
    public async Task Only_agentforge_images_can_be_built_and_unlisted_ones_are_refused()
    {
        var other = await Assert.ThrowsAsync<ProcessStartException>(() => _remote.EnsureImageAsync("minha/aprovada:1", "FROM x\n", false, default));
        var unlisted = await Assert.ThrowsAsync<ProcessStartException>(() => _remote.EnsureImageAsync("alpine:3", null, false, default));

        Assert.Contains("agentforge/*", other.Message);
        Assert.Contains("não permitida", unlisted.Message);
        Assert.DoesNotContain("ARGS build", Log());
    }

    // ------------------------------------------------------------------ API e runner

    private async Task<Guid> CreateEnvironment(string url) => (await (await _http.PostAsJsonAsync("/api/environments",
        new { name = "docker-" + Guid.NewGuid().ToString("N")[..6], url, token = Token, workspaceRoot = Root, tags = "node22" }))
        .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private async Task<JsonElement> Profiles() => await (await _http.GetAsync("/api/stack-profiles")).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<Guid> ProfileId(string name) =>
        (await Profiles()).EnumerateArray().Single(p => p.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    [PosixFact]
    public async Task The_ready_made_profiles_exist_run_the_agent_inside_and_cannot_be_changed()
    {
        var profiles = (await Profiles()).EnumerateArray().ToList();

        Assert.Equal(new[] { "Node 22", "Python 3.12", ".NET 8" }.Order(), profiles.Where(p => p.GetProperty("builtIn").GetBoolean()).Select(p => p.GetProperty("name").GetString()!).Order());
        var node = profiles.Single(p => p.GetProperty("name").GetString() == "Node 22");
        var dockerfile = node.GetProperty("dockerfile").GetString()!;
        Assert.StartsWith("FROM node:22", dockerfile);
        Assert.Contains("@anthropic-ai/claude-code@", dockerfile);        // o agente roda DENTRO do container
        Assert.Contains("CLAUDE_CONFIG_DIR=/agent-config", dockerfile);

        var id = node.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PutAsJsonAsync($"/api/stack-profiles/{id}", new { name = "x", image = "a:1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.DeleteAsync($"/api/stack-profiles/{id}")).StatusCode);
    }

    [PosixFact]
    public async Task User_profiles_are_validated_unique_and_protected_while_in_use()
    {
        foreach (var bad in new object[]
        {
            new { name = "", image = "a:1" }, new { name = "x" }, new { name = "x", image = "a:1", dockerfile = "FROM a" },
            new { name = "x", image = "--privileged" }, new { name = "x", image = "a:1", network = "host" },
            new { name = "x", image = "a:1", cpuLimit = -1 }, new { name = "x", image = "a:1", memoryMb = -1 },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/stack-profiles", bad)).StatusCode);

        var created = await _http.PostAsJsonAsync("/api/stack-profiles", new { name = "Meu Go", dockerfile = "FROM golang:1.23\n", network = "none", cpuLimit = 1.5, memoryMb = 2048 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsJsonAsync("/api/stack-profiles", new { name = "Meu Go", image = "a:1" })).StatusCode);

        var env = await CreateEnvironment(_agent!.Urls.First());
        var project = await _http.PostAsJsonAsync("/api/projects", new { name = "P", repositoryUrl = "https://h/r.git", environmentId = env, stackProfileId = id });
        Assert.Equal(HttpStatusCode.Created, project.StatusCode);
        Assert.Equal(id, (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stackProfileId").GetGuid());

        var inUse = await _http.DeleteAsync($"/api/stack-profiles/{id}");
        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Contains("1 projeto", await inUse.Content.ReadAsStringAsync());
    }

    [PosixFact]
    public async Task A_project_with_a_profile_needs_an_environment_and_an_existing_profile()
    {
        var node = await ProfileId("Node 22");

        var noEnv = await _http.PostAsJsonAsync("/api/projects", new { name = "P", repositoryUrl = "https://h/r.git", stackProfileId = node });
        var ghost = await _http.PostAsJsonAsync("/api/projects", new { name = "P", repositoryUrl = "https://h/r.git",
            environmentId = await CreateEnvironment(_agent!.Urls.First()), stackProfileId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.BadRequest, noEnv.StatusCode);
        Assert.Contains("escolha também o ambiente", await noEnv.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, ghost.StatusCode);
    }

    [PosixFact]
    public async Task The_environment_test_says_whether_containers_are_enabled()
    {
        var withDocker = await CreateEnvironment(_agent!.Urls.First());
        var without = await CreateEnvironment(_plain!.Urls.First());

        var on = await (await _http.PostAsync($"/api/environments/{withDocker}/test", null)).Content.ReadFromJsonAsync<JsonElement>();
        var off = await (await _http.PostAsync($"/api/environments/{without}/test", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(on.GetProperty("containersEnabled").GetBoolean());
        Assert.False(off.GetProperty("containersEnabled").GetBoolean());
    }

    [PosixFact]
    public async Task Building_a_profile_on_an_environment_shows_the_log_and_failures_come_back_as_text()
    {
        var env = await CreateEnvironment(_agent!.Urls.First());
        var node = await ProfileId("Node 22");

        var built = await (await _http.PostAsync($"/api/stack-profiles/{node}/build?environmentId={env}", null)).Content.ReadFromJsonAsync<JsonElement>();
        var again = await (await _http.PostAsync($"/api/stack-profiles/{node}/build?environmentId={env}", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(built.GetProperty("ok").GetBoolean());
        Assert.True(built.GetProperty("built").GetBoolean());
        Assert.Contains("Step 1/1", built.GetProperty("log").GetString());
        Assert.StartsWith("agentforge/stack:", built.GetProperty("image").GetString());
        Assert.False(again.GetProperty("built").GetBoolean());

        var offline = await CreateEnvironment("http://127.0.0.1:1");
        var failed = await (await _http.PostAsync($"/api/stack-profiles/{node}/build?environmentId={offline}", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Contains("inacessível", failed.GetProperty("log").GetString());
    }

    private async Task<(CliAgentRunner Runner, IServiceScope Scope)> Runner()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (new CliAgentRunner(new AgentAdapterRegistry([new FakeCliAgentAdapter("echo oi")]), db, new CollectingPublisher(),
            scope.ServiceProvider.GetRequiredService<IEnvironmentLaunchers>(), NullLogger<CliAgentRunner>.Instance), scope);
    }

    private async Task<Guid> ProjectWith(Guid? env, Guid? profile)
    {
        var response = await _http.PostAsJsonAsync("/api/projects", new { name = "P" + Guid.NewGuid().ToString("N")[..6], repositoryUrl = "https://h/r.git", environmentId = env, stackProfileId = profile });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [PosixFact]
    public async Task The_runner_builds_the_image_then_runs_the_project_agent_in_its_profile_container()
    {
        var env = await CreateEnvironment(_agent!.Urls.First());
        var profile = await ProfileId("Node 22");
        var project = await ProjectWith(env, profile);
        var work = $"{ProjectDir(project)}/worktrees/t1";
        Directory.CreateDirectory(work);
        var (runner, scope) = await Runner();
        using var _ = scope;

        var run = await runner.RunAsync(new AgentRequest(AgentRole.Dev, "faça", work, "fake", ProjectId: project), default);

        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        var log = Log();
        Assert.Contains("ARGS build", log);                                            // primeira vez: constrói
        var dockerfile = (await Profiles()).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == profile).GetProperty("dockerfile").GetString()!;
        var tag = ContainerRules.ImageTag(dockerfile);
        Assert.Contains($"{tag} ", log.Split('\n').Single(l => l.StartsWith("ARGS run ")));
        Assert.Contains($"-v {ProjectDir(project)}:{ProjectDir(project)}", log);        // monta só a pasta DO PROJETO
        Assert.Contains("--cpus 2", log);                                              // limites do perfil
        Assert.Contains("--memory 4096m", log);

        await runner.RunAsync(new AgentRequest(AgentRole.Dev, "de novo", work, "fake", ProjectId: project), default);
        Assert.Single(log.Split('\n').Where(l => l.StartsWith("ARGS build")));          // a imagem já existe: não reconstrói
    }

    [PosixFact]
    public async Task Projects_without_a_profile_keep_running_directly_on_the_machine()
    {
        var env = await CreateEnvironment(_agent!.Urls.First());
        var project = await ProjectWith(env, null);
        Directory.CreateDirectory(Root);
        var (runner, scope) = await Runner();
        using var _ = scope;

        var run = await runner.RunAsync(new AgentRequest(AgentRole.Dev, "x", Root, "fake", ProjectId: project), default);

        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.DoesNotContain("ARGS run", Log());
    }

    [PosixFact]
    public async Task A_profile_on_a_host_agent_without_docker_or_a_missing_profile_fails_the_run_with_the_reason()
    {
        var plainEnv = await CreateEnvironment(_plain!.Urls.First());
        var profile = await ProfileId("Node 22");
        var project = await ProjectWith(plainEnv, profile);
        var work = $"{ProjectDir(project)}/worktrees/t1";
        Directory.CreateDirectory(work);
        var (runner, scope) = await Runner();
        using var _ = scope;

        var run = await runner.RunAsync(new AgentRequest(AgentRole.Dev, "x", work, "fake", ProjectId: project), default);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains("não estão habilitados", run.Output);

        // perfil apagado por fora (FK restrita impede pela API): a execução avisa em vez de rodar fora do container
        await using (var db = await Db.CreateAsync(_cs))
        {
            await db.Database.ExecuteSqlRawAsync("""UPDATE "Projects" SET "StackProfileId" = NULL WHERE "Id" = {0}""", project);
            var orphan = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync("""ALTER TABLE "Projects" DROP CONSTRAINT IF EXISTS "FK_Projects_StackProfiles_StackProfileId" """);
            await db.Database.ExecuteSqlRawAsync("""UPDATE "Projects" SET "StackProfileId" = {0} WHERE "Id" = {1}""", orphan, project);
        }
        var missing = await runner.RunAsync(new AgentRequest(AgentRole.Dev, "x", work, "fake", ProjectId: project), default);
        Assert.Equal(AgentRunStatus.Failed, missing.Status);
        Assert.Contains("não existe mais", missing.Output);
    }

    [PosixFact]
    public async Task The_default_server_environment_cannot_run_containers()
    {
        var local = new LocalProcessLauncher();

        var ex = await Assert.ThrowsAsync<ProcessStartException>(() => local.StartAsync(new LaunchSpec("claude", [], Root, Container: Spec(Root)), default));

        Assert.Contains("host agent com Docker", ex.Message);
        await Task.CompletedTask;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition(), "condição não atingida a tempo");
    }
}
