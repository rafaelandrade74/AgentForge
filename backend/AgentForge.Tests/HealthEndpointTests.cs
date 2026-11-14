using System.Net;

namespace AgentForge.Tests;

public class HealthEndpointTests
{
    [Fact]
    public async Task Get_health_returns_200_without_token()
    {
        await using var factory = new TestApiFactory();

        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
