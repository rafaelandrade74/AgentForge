using System.Net;
using System.Net.Http.Json;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests;

public sealed class EnvironmentMigrationTests : IAsyncLifetime
{
    private TestApiFactory _factory = null!;
    private HttpClient _client = null!;
    private readonly FakeWorkspaces _workspaces = new();
    private readonly FakePushUrl _pushUrl = new();

    public async Task InitializeAsync()
    {
        _factory = new TestApiFactory().WithServices(services =>
        {
            services.AddSingleton<IWorkspaceManager>(_workspaces);
            services.AddSingleton<IPushUrlProvider>(_pushUrl);
        });
        _client = await TestAuth.AuthenticatedClientAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> SeedProjectAsync(Guid? environmentId = null)
    {
        var id = Guid.NewGuid();
        await _factory.SeedAsync(async db =>
        {
            db.Projects.Add(new Project { Id = id, Name = "P1", RepositoryUrl = "git@github.com:o/r.git", EnvironmentId = environmentId });
            await db.SaveChangesAsync();
        });
        return id;
    }

    private async Task<Guid> SeedEnvironmentAsync(string name = "Env")
    {
        var id = Guid.NewGuid();
        await _factory.SeedAsync(async db =>
        {
            db.ExecutionEnvironments.Add(new ExecutionEnvironment { Id = id, Name = name, Url = "http://host:8787", ProtectedToken = "tok", WorkspaceRoot = "/data" });
            await db.SaveChangesAsync();
        });
        return id;
    }

    [Fact]
    public async Task Rejects_when_target_equals_source()
    {
        var envId = await SeedEnvironmentAsync();
        var projId = await SeedProjectAsync(envId);
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projId}/migrate-environment", new { targetEnvironmentId = envId });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Rejects_when_target_not_found()
    {
        var projId = await SeedProjectAsync();
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projId}/migrate-environment", new { targetEnvironmentId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Rejects_when_agents_running()
    {
        var envA = await SeedEnvironmentAsync("A");
        var envB = await SeedEnvironmentAsync("B");
        var projId = await SeedProjectAsync(envA);
        var demandId = Guid.NewGuid();
        await _factory.SeedAsync(async db =>
        {
            db.Demands.Add(new Demand { Id = demandId, ProjectId = projId, Title = "D" });
            db.AgentRuns.Add(new AgentRun { DemandId = demandId, Status = AgentRunStatus.Running, Role = AgentRole.ScrumMaster });
            await db.SaveChangesAsync();
        });

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projId}/migrate-environment", new { targetEnvironmentId = envB });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Rejects_when_no_push_url()
    {
        var envA = await SeedEnvironmentAsync("A");
        var envB = await SeedEnvironmentAsync("B");
        var projId = await SeedProjectAsync(envA);
        _pushUrl.Result = new PushUrlResult(null, null, "sem token");

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projId}/migrate-environment", new { targetEnvironmentId = envB });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task Migrates_active_branches_and_updates_project()
    {
        var envA = await SeedEnvironmentAsync("A");
        var envB = await SeedEnvironmentAsync("B");
        var projId = await SeedProjectAsync(envA);
        var demandId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await _factory.SeedAsync(async db =>
        {
            db.Demands.Add(new Demand { Id = demandId, ProjectId = projId, Title = "D", Status = DemandStatus.InProgress, BranchName = $"agentforge/demand-{demandId}" });
            db.WorkItems.Add(new WorkItem { Id = taskId, DemandId = demandId, Title = "T", State = WorkItemState.InDevelopment, BranchName = $"agentforge/task-{taskId}" });
            await db.SaveChangesAsync();
        });

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projId}/migrate-environment", new { targetEnvironmentId = envB });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<MigrationResultDto>();
        Assert.NotNull(result);
        Assert.Equal(2, result.BranchesPushed);
        Assert.Equal(0, result.BranchesSkipped);

        // Verifica que o projeto agora aponta para envB
        using var scope = _factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = await db2.Projects.FindAsync(projId);
        Assert.Equal(envB, project!.EnvironmentId);

        // Verifica que o WorkspaceManager foi chamado
        Assert.True(_workspaces.EnsureRepoCalls >= 2); // source + target
        Assert.Contains($"agentforge/demand-{demandId}", _workspaces.LocalBranchesCreated);
        Assert.Contains($"agentforge/task-{taskId}", _workspaces.LocalBranchesCreated);
    }

    [Fact]
    public async Task Migrate_to_default_environment()
    {
        var envA = await SeedEnvironmentAsync("A");
        var projId = await SeedProjectAsync(envA);

        var resp = await _client.PostAsJsonAsync($"/api/projects/{projId}/migrate-environment",
            new { targetEnvironmentId = (Guid?)null });
        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var project = await db2.Projects.FindAsync(projId);
        Assert.Null(project!.EnvironmentId);
    }

    private record MigrationResultDto(int BranchesPushed, int BranchesSkipped, List<string> Warnings);

    private sealed class FakeWorkspaces : IWorkspaceManager
    {
        public int EnsureRepoCalls;
        public int PushCalls;
        public List<string> LocalBranchesCreated = [];

        public Task<string> EnsureRepositoryAsync(Project project, CancellationToken ct) { EnsureRepoCalls++; return Task.FromResult("/fake/repo"); }
        public Task<string> EnsureDemandBranchAsync(Project project, Guid demandId, CancellationToken ct) => Task.FromResult("branch");
        public Task<Workspace> CreateWorktreeAsync(Project project, Guid workItemId, Guid demandId, CancellationToken ct, string? baseCommit = null) => throw new NotSupportedException();
        public Task<string> MergeTaskIntoDemandAsync(Project project, Guid demandId, Guid workItemId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveWorktreeAsync(Project project, Guid workItemId, bool deleteBranch, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetDiffAsync(Project project, Guid workItemId, CancellationToken ct, string? baseCommit = null) => throw new NotSupportedException();
        public Task PushTaskBranchAsync(Project project, Guid workItemId, string pushUrl, CancellationToken ct) { PushCalls++; return Task.CompletedTask; }
        public Task PushDemandBranchAsync(Project project, Guid demandId, string pushUrl, CancellationToken ct) { PushCalls++; return Task.CompletedTask; }
        public Task<bool> IsBranchPublishedAsync(Project project, Guid workItemId, string pushUrl, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> IsDemandBranchPublishedAsync(Project project, Guid demandId, string pushUrl, CancellationToken ct) => Task.FromResult(false);
        public Task<int> EnsureLocalBranchesFromRemoteAsync(Project project, IReadOnlyList<string> branchNames, CancellationToken ct)
        {
            LocalBranchesCreated.AddRange(branchNames);
            return Task.FromResult(branchNames.Count);
        }
    }

    private sealed class FakePushUrl : IPushUrlProvider
    {
        public PushUrlResult Result { get; set; } = new("git@github.com:o/r.git", null, null);
        public Task<PushUrlResult> ResolveAsync(Project project, CancellationToken ct) => Task.FromResult(Result);
    }
}
