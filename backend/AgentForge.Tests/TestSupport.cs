using AgentForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentForge.Tests;

/// <summary>Detecção estática de Postgres: variável de ambiente ou Docker acessível.</summary>
public static class PostgresAvailability
{
    public static readonly bool IsAvailable;
    public static readonly string SkipReason = "Postgres não disponível (sem Docker e sem AGENTFORGE_TEST_PG).";

    static PostgresAvailability()
    {
        if (Environment.GetEnvironmentVariable("AGENTFORGE_TEST_PG") is not null)
        {
            IsAvailable = true;
            return;
        }
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker", Arguments = "info",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            if (proc is not null)
            {
                proc.WaitForExit(5000);
                IsAvailable = proc.ExitCode == 0;
            }
        }
        catch
        {
            IsAvailable = false;
        }
    }
}

/// <summary>Um único Postgres 16 para todos os testes de integração; cada classe cria o próprio banco dentro dele.</summary>
public class PostgresFixture : IAsyncLifetime
{
    private static readonly string? External = Environment.GetEnvironmentVariable("AGENTFORGE_TEST_PG");

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    public bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        if (!PostgresAvailability.IsAvailable) return;
        if (External is not null)
        {
            _connectionString = External;
            return;
        }
        try
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").WithCommand("-c", "max_connections=500").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch
        {
            _container = null;
            _connectionString = null;
        }
    }

    public Task DisposeAsync() => _container?.DisposeAsync().AsTask() ?? Task.CompletedTask;

    public string NewConnectionString() =>
        _connectionString is null
            ? throw new InvalidOperationException(PostgresAvailability.SkipReason)
            : new NpgsqlConnectionStringBuilder(_connectionString)
            {
                Database = "t_" + Guid.NewGuid().ToString("N"),
                MaxPoolSize = 6, ConnectionIdleLifetime = 5, ConnectionPruningInterval = 1,
            }.ConnectionString;
}

[CollectionDefinition(Name)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>Fact que skippa automaticamente quando Postgres não está disponível.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (!PostgresAvailability.IsAvailable) Skip = PostgresAvailability.SkipReason;
        else if (OperatingSystem.IsWindows()) Skip = "Requer Linux/macOS.";
    }
}

/// <summary>Theory que skippa automaticamente quando Postgres não está disponível.</summary>
public sealed class IntegrationTheoryAttribute : TheoryAttribute
{
    public IntegrationTheoryAttribute()
    {
        if (!PostgresAvailability.IsAvailable) Skip = PostgresAvailability.SkipReason;
        else if (OperatingSystem.IsWindows()) Skip = "Requer Linux/macOS.";
    }
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
