using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AgentForge.Tests;

[Collection(PostgresCollection.Name)]
public class AuthTests : IDisposable
{
    private record Tokens(string AccessToken, string RefreshToken);

    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public AuthTests(PostgresFixture pg)
    {
        _factory = new ApiFactory(pg.NewConnectionString());
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Tokens> LoginAsync()
    {
        var res = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = ApiFactory.AdminPassword });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<Tokens>())!;
    }

    [IntegrationFact]
    public async Task Protected_route_without_token_returns_401()
    {
        var res = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [IntegrationFact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var res = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "errada" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [IntegrationFact]
    public async Task Login_then_access_protected_route()
    {
        var tokens = await LoginAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [IntegrationFact]
    public async Task Refresh_rotates_token_and_old_one_is_rejected()
    {
        var tokens = await LoginAsync();

        var first = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        var replay = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }
}
