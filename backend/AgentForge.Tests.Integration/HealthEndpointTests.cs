namespace AgentForge.Tests.Integration;

public class HealthEndpointTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [IntegrationFact]
    public async Task Get_health_returns_200_without_token()
    {
        await using var factory = new ApiFactory(pg.NewConnectionString());

        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
