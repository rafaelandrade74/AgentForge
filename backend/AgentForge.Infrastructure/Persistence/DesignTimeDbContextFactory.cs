using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentForge.Infrastructure.Persistence;

/// <summary>Usado só pelo `dotnet ef`; evita subir a API (e conectar no banco) para gerar migrations.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=agentforge_design").Options);
}
