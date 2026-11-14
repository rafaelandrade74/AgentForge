using System.Net.Http.Json;
using AgentForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Tests;

/// <summary>
/// WebApplicationFactory que usa SQLite in-memory no lugar do Postgres.
/// SQLite suporta ExecuteUpdate/ExecuteDelete, ao contrário do InMemory provider.
/// </summary>
public class TestApiFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "s3nha-de-teste";
    private readonly SqliteConnection _connection;
    private Action<IServiceCollection>? _configureServices;

    public TestApiFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public TestApiFactory WithServices(Action<IServiceCollection> configure)
    {
        _configureServices = configure;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:JwtSecret", new string('k', 40));
        builder.UseSetting("Auth:AdminUsername", "admin");
        builder.UseSetting("Auth:AdminPassword", AdminPassword);
        builder.UseSetting("Prompts:Directory", Path.Combine(AppContext.BaseDirectory, "no-prompts"));

        builder.ConfigureServices(services =>
        {
            var toRemove = services.Where(d =>
                d.ServiceType == typeof(AppDbContext) ||
                d.ServiceType == typeof(DbContextOptions<AppDbContext>)).ToList();
            foreach (var d in toRemove) services.Remove(d);

            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection).ReplaceService<IValueConverterSelector, DecimalToDoubleConverterSelector>());

            _configureServices?.Invoke(services);
        });
    }

    public async Task SeedAsync(Func<AppDbContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await seed(db);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

public class DecimalToDoubleConverterSelector : ValueConverterSelector
{
    public DecimalToDoubleConverterSelector(ValueConverterSelectorDependencies dependencies) : base(dependencies) { }

    public override IEnumerable<ValueConverterInfo> Select(Type modelClrType, Type? providerClrType = null)
    {
        var underlyingModelType = Nullable.GetUnderlyingType(modelClrType) ?? modelClrType;
        if (underlyingModelType == typeof(decimal))
        {
            yield return new ValueConverterInfo(modelClrType, typeof(double),
                i => modelClrType == typeof(decimal)
                    ? new CastingConverter<decimal, double>(i.MappingHints)
                    : new CastingConverter<decimal?, double?>(i.MappingHints));
            yield break;
        }
        foreach (var info in base.Select(modelClrType, providerClrType))
            yield return info;
    }
}

public static class TestAuth
{
    public static async Task<string> LoginAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "admin", password = TestApiFactory.AdminPassword });
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return json.GetProperty("accessToken").GetString()!;
    }

    public static async Task<HttpClient> AuthenticatedClientAsync(TestApiFactory factory)
    {
        var client = factory.CreateClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
