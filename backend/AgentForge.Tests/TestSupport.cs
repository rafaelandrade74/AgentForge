using AgentForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentForge.Tests;

/// <summary>Um único Postgres 16 para todos os testes; cada classe cria o próprio banco dentro dele.</summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public string NewConnectionString() =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        { Database = "t_" + Guid.NewGuid().ToString("N") }.ConnectionString;
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

public class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string AdminPassword = "s3nha-de-teste";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Auth:JwtSecret", new string('k', 40));
        builder.UseSetting("Auth:AdminUsername", "admin");
        builder.UseSetting("Auth:AdminPassword", AdminPassword);
        builder.UseSetting("Prompts:Directory", Path.Combine(AppContext.BaseDirectory, "no-prompts"));
    }
}

public static class Db
{
    public static async Task<AppDbContext> CreateAsync(string connectionString)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync();
        return db;
    }
}
