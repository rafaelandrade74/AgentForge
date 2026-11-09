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
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<ExecutionEnvironment> ExecutionEnvironments => Set<ExecutionEnvironment>();
    public DbSet<StackProfile> StackProfiles => Set<StackProfile>();
    public DbSet<ProjectRoleEnvironment> ProjectRoleEnvironments => Set<ProjectRoleEnvironment>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b) =>
        b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Project>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.RepositoryUrl).HasMaxLength(500);
            e.Property(x => x.BaseBranch).HasMaxLength(200);
            e.Property(x => x.PullRequestBaseBranch).HasMaxLength(200);
            e.Property(x => x.ProtectedGitHubToken).HasMaxLength(2000);
            // um ambiente em uso não pode ser apagado (o clone do projeto mora nele)
            e.HasOne<ExecutionEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StackProfile>().WithMany().HasForeignKey(x => x.StackProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ProjectRoleEnvironment>(e =>
        {
            e.HasKey(x => new { x.ProjectId, x.Role });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ExecutionEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ChatSession>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.AdapterName).HasMaxLength(100);
            e.Property(x => x.ProviderSessionId).HasMaxLength(200);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UpdatedAt);
        });

        b.Entity<ChatMessage>(e =>
        {
            e.HasOne<ChatSession>().WithMany().HasForeignKey(x => x.ChatSessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ChatSessionId, x.CreatedAt });
        });

        b.Entity<StackProfile>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Image).HasMaxLength(300);
            e.Property(x => x.Dockerfile).HasMaxLength(20000);
            e.Property(x => x.Network).HasMaxLength(16);
        });

        b.Entity<ExecutionEnvironment>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Url).HasMaxLength(300);
            e.Property(x => x.ProtectedToken).HasMaxLength(2000);
            e.Property(x => x.WorkspaceRoot).HasMaxLength(500);
            e.Property(x => x.Tags).HasMaxLength(300);
            e.Property(x => x.MaxConcurrency).HasDefaultValue(1);
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
            e.Property(x => x.WaitingReason).HasMaxLength(500);
            e.Property(x => x.PullRequestUrl).HasMaxLength(500);
            e.Property(x => x.PushError).HasMaxLength(500);
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

        b.Entity<ApiToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Prefix).HasMaxLength(16);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.Username).HasMaxLength(200);
        });
    }
}
