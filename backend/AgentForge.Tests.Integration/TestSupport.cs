using AgentForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AgentForge.Tests.Integration;

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
                FileName = "docker", Arguments = "version --format '{{.Server.Version}}'",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            });
            if (proc is not null)
            {
                IsAvailable = proc.WaitForExit(10_000) && proc.ExitCode == 0;
                if (!proc.HasExited) try { proc.Kill(); } catch { }
            }
        }
        catch
        {
            IsAvailable = false;
        }
    }
}

/// <summary>Singleton: um único Postgres 16 para todos os testes; cada classe cria o próprio banco.</summary>
public static class SharedPostgres
{
    private static readonly SemaphoreSlim Lock = new(1, 1);
    private static PostgreSqlContainer? _container;
    private static string? _connectionString;
    private static bool _initialized;

    public static async Task<string> GetConnectionStringAsync()
    {
        if (_initialized) return _connectionString ?? throw new InvalidOperationException(PostgresAvailability.SkipReason);

        await Lock.WaitAsync();
        try
        {
            if (_initialized) return _connectionString ?? throw new InvalidOperationException(PostgresAvailability.SkipReason);

            var external = Environment.GetEnvironmentVariable("AGENTFORGE_TEST_PG");
            if (external is not null)
            {
                _connectionString = external;
                _initialized = true;
                return _connectionString;
            }

            _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").WithCommand("-c", "max_connections=500").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
            _initialized = true;

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { _container?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            };

            return _connectionString;
        }
        finally
        {
            Lock.Release();
        }
    }

    public static async Task<string> NewConnectionStringAsync()
    {
        var cs = await GetConnectionStringAsync();
        return new NpgsqlConnectionStringBuilder(cs)
        {
            Database = "t_" + Guid.NewGuid().ToString("N"),
            MaxPoolSize = 6, ConnectionIdleLifetime = 5, ConnectionPruningInterval = 1,
        }.ConnectionString;
    }
}

/// <summary>Wrapper por classe — delega ao singleton SharedPostgres; permite IClassFixture injeção.</summary>
public class PostgresFixture : IAsyncLifetime
{
    public Task InitializeAsync() => PostgresAvailability.IsAvailable ? SharedPostgres.GetConnectionStringAsync() : Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    public string NewConnectionString() =>
        SharedPostgres.NewConnectionStringAsync().GetAwaiter().GetResult();
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
    private static readonly SemaphoreSlim MigrateLock = new(3, 3);

    public static async Task<AppDbContext> CreateAsync(string connectionString)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
        await MigrateLock.WaitAsync();
        try { await db.Database.MigrateAsync(); }
        finally { MigrateLock.Release(); }
        return db;
    }
}
