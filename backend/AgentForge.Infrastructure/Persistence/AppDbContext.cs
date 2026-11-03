using AgentForge.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Demand> Demands => Set<Demand>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<ReviewFinding> ReviewFindings => Set<ReviewFinding>();
    public DbSet<AgentRoleConfig> AgentRoleConfigs => Set<AgentRoleConfig>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b) =>
        b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Project>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.RepositoryUrl).HasMaxLength(500);
            e.Property(x => x.BaseBranch).HasMaxLength(200);
        });

        b.Entity<Demand>(e =>
        {
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Title).HasMaxLength(300);
        });

        b.Entity<WorkItem>(e =>
        {
            e.HasOne<Demand>().WithMany().HasForeignKey(x => x.DemandId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.BranchName).HasMaxLength(200);
            e.HasMany(x => x.Dependencies).WithMany()
                .UsingEntity("WorkItemDependencies",
                    l => l.HasOne(typeof(WorkItem)).WithMany().HasForeignKey("DependsOnId").OnDelete(DeleteBehavior.Cascade),
                    r => r.HasOne(typeof(WorkItem)).WithMany().HasForeignKey("WorkItemId").OnDelete(DeleteBehavior.Cascade));
        });

        b.Entity<AgentRun>(e =>
        {
            e.HasOne<Demand>().WithMany().HasForeignKey(x => x.DemandId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.CostUsd).HasPrecision(12, 6);
        });

        b.Entity<ReviewFinding>(e =>
        {
            e.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AgentRun>().WithMany().HasForeignKey(x => x.AgentRunId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.File).HasMaxLength(500);
        });

        b.Entity<AgentRoleConfig>(e =>
        {
            e.HasIndex(x => x.Role).IsUnique();
            e.Property(x => x.AdapterName).HasMaxLength(100);
            e.Property(x => x.Model).HasMaxLength(100);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Username).HasMaxLength(200);
        });
    }
}
