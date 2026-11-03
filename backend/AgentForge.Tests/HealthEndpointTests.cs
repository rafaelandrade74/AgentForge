using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentForge.Tests;

public class HealthEndpointTests : IClassFixture<HealthEndpointTests.Factory>
{
    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("Auth:JwtSecret", new string('k', 40));
            builder.UseSetting("Auth:AdminPassword", "s3nha-de-teste");
        }
    }

    private readonly Factory _factory;

    public HealthEndpointTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Get_health_returns_200_without_token()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
