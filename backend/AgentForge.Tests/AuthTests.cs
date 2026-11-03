using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentForge.Tests;

public class AuthTests : IClassFixture<AuthTests.Factory>
{
    public class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("Auth:JwtSecret", new string('k', 40));
            builder.UseSetting("Auth:AdminUsername", "admin");
            builder.UseSetting("Auth:AdminPassword", "s3nha-de-teste");
        }
    }

    private record Tokens(string AccessToken, string RefreshToken);

    private readonly HttpClient _client;

    public AuthTests(Factory factory) => _client = factory.CreateClient();

    private async Task<Tokens> LoginAsync()
    {
        var res = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "s3nha-de-teste" });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<Tokens>())!;
    }

    [Fact]
    public async Task Protected_route_without_token_returns_401()
    {
        var res = await _client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var res = await _client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "errada" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_then_access_protected_route()
    {
        var tokens = await LoginAsync();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_token_and_old_one_is_rejected()
    {
        var tokens = await LoginAsync();

        var first = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        var replay = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }
}
