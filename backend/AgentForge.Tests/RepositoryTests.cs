using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Tests;

public class RepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public RepositoryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Persists_full_graph_and_reads_it_back()
    {
        var project = new Project { Name = "P", RepositoryUrl = "u" };
        var demand = new Demand { ProjectId = project.Id, Title = "D" };
        var dep = new WorkItem { DemandId = demand.Id, Title = "dep" };
        var item = new WorkItem { DemandId = demand.Id, Title = "item", State = WorkItemState.TechLeadReview, Dependencies = [dep] };
        var run = new AgentRun { WorkItemId = item.Id, Role = AgentRole.Dev, Prompt = "p", CostUsd = 0.123456m, Status = AgentRunStatus.Succeeded };
        var finding = new ReviewFinding { WorkItemId = item.Id, AgentRunId = run.Id, Role = AgentRole.TechLead, Message = "m", Line = 3 };
        _db.AddRange(project, demand, dep, item, run, finding);
        await _db.SaveChangesAsync();

        var loaded = await _db.WorkItems.Include(w => w.Dependencies).SingleAsync(w => w.Title == "item");
        Assert.Equal(WorkItemState.TechLeadReview, loaded.State);
        Assert.Single(loaded.Dependencies);
        Assert.Equal(0.123456m, (await _db.AgentRuns.SingleAsync()).CostUsd);
        Assert.Equal(3, (await _db.ReviewFindings.SingleAsync()).Line);
    }

    [Fact]
    public void Enums_are_configured_with_string_conversion_and_max_length()
    {
        var entity = _db.Model.FindEntityType(typeof(WorkItem))!;
        var stateProp = entity.FindProperty(nameof(WorkItem.State))!;
        Assert.Equal(32, stateProp.GetMaxLength());

        var demandEntity = _db.Model.FindEntityType(typeof(Demand))!;
        var statusProp = demandEntity.FindProperty(nameof(Demand.Status))!;
        Assert.Equal(32, statusProp.GetMaxLength());
    }

    [Fact]
    public async Task Deleting_project_cascades_to_demands_and_work_items()
    {
        var project = new Project { Name = "P", RepositoryUrl = "u" };
        var demand = new Demand { ProjectId = project.Id, Title = "D" };
        _db.AddRange(project, demand, new WorkItem { DemandId = demand.Id, Title = "W" });
        await _db.SaveChangesAsync();

        _db.Projects.Remove(project);
        await _db.SaveChangesAsync();

        Assert.Empty(await _db.Demands.ToListAsync());
        Assert.Empty(await _db.WorkItems.ToListAsync());
    }

    [Fact]
    public void Role_config_has_unique_index_on_role()
    {
        var model = _db.Model;
        var entity = model.FindEntityType(typeof(AgentRoleConfig))!;
        var index = entity.GetIndexes().SingleOrDefault(i => i.Properties.Any(p => p.Name == nameof(AgentRoleConfig.Role)));
        Assert.NotNull(index);
        Assert.True(index.IsUnique);
    }
}
