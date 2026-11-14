using AgentForge.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Tests.Integration;

[Collection(PostgresCollection.Name)]
public class RepositoryTests(PostgresFixture pg)
{
    [IntegrationFact]
    public async Task Persists_full_graph_and_reads_it_back()
    {
        var cs = pg.NewConnectionString();
        await using (var db = await Db.CreateAsync(cs))
        {
            var project = new Project { Name = "P", RepositoryUrl = "u" };
            var demand = new Demand { ProjectId = project.Id, Title = "D" };
            var dep = new WorkItem { DemandId = demand.Id, Title = "dep" };
            var item = new WorkItem { DemandId = demand.Id, Title = "item", State = WorkItemState.TechLeadReview, Dependencies = [dep] };
            var run = new AgentRun { WorkItemId = item.Id, Role = AgentRole.Dev, Prompt = "p", CostUsd = 0.123456m, Status = AgentRunStatus.Succeeded };
            var finding = new ReviewFinding { WorkItemId = item.Id, AgentRunId = run.Id, Role = AgentRole.TechLead, Message = "m", Line = 3 };
            db.AddRange(project, demand, dep, item, run, finding);
            await db.SaveChangesAsync();
        }

        await using var read = await Db.CreateAsync(cs);
        var loaded = await read.WorkItems.Include(w => w.Dependencies).SingleAsync(w => w.Title == "item");
        Assert.Equal(WorkItemState.TechLeadReview, loaded.State);
        Assert.Single(loaded.Dependencies);
        Assert.Equal(0.123456m, (await read.AgentRuns.SingleAsync()).CostUsd);
        Assert.Equal(3, (await read.ReviewFindings.SingleAsync()).Line);
    }

    [IntegrationFact]
    public async Task Enums_are_stored_as_text()
    {
        await using var db = await Db.CreateAsync(pg.NewConnectionString());
        var project = new Project { Name = "P", RepositoryUrl = "u" };
        db.Add(project);
        db.Add(new Demand { ProjectId = project.Id, Title = "D", Status = DemandStatus.InProgress });
        await db.SaveChangesAsync();

        var raw = await db.Database.SqlQueryRaw<string>("""SELECT "Status" AS "Value" FROM "Demands" """).SingleAsync();

        Assert.Equal("InProgress", raw);
    }

    [IntegrationFact]
    public async Task Deleting_project_cascades_to_demands_and_work_items()
    {
        await using var db = await Db.CreateAsync(pg.NewConnectionString());
        var project = new Project { Name = "P", RepositoryUrl = "u" };
        var demand = new Demand { ProjectId = project.Id, Title = "D" };
        db.AddRange(project, demand, new WorkItem { DemandId = demand.Id, Title = "W" });
        await db.SaveChangesAsync();

        await db.Projects.ExecuteDeleteAsync();

        Assert.Empty(await db.Demands.ToListAsync());
        Assert.Empty(await db.WorkItems.ToListAsync());
    }

    [IntegrationFact]
    public async Task Role_config_role_is_unique()
    {
        await using var db = await Db.CreateAsync(pg.NewConnectionString());
        db.AddRange(new AgentRoleConfig { Role = AgentRole.Dev }, new AgentRoleConfig { Role = AgentRole.Dev });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
