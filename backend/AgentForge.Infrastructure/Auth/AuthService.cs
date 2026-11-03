using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AgentForge.Infrastructure.Auth;

public record TokenPair(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc);

/// <summary>
/// Usuário único semeado por variável de ambiente. O hash BCrypt é gerado no start;
/// refresh tokens ficam em memória (rotacionados a cada uso) até a Fase 2 trazer o banco.
/// </summary>
public class AuthService
{
    private record RefreshEntry(string Username, DateTime ExpiresAtUtc);

    private readonly AuthOptions _options;
    private readonly TimeProvider _time;
    private readonly string _adminHash;
    private readonly ConcurrentDictionary<string, RefreshEntry> _refreshTokens = new();

    public AuthService(IOptions<AuthOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
        _adminHash = BCrypt.Net.BCrypt.HashPassword(_options.AdminPassword);
    }

    public TokenPair? Login(string username, string password)
    {
        // Sempre verifica o hash, para não distinguir usuário inexistente por tempo de resposta.
        var passwordOk = BCrypt.Net.BCrypt.Verify(password, _adminHash);
        var userOk = string.Equals(username, _options.AdminUsername, StringComparison.Ordinal);
        return passwordOk && userOk ? Issue(_options.AdminUsername) : null;
    }

    public TokenPair? Refresh(string refreshToken)
    {
        var key = Hash(refreshToken);
        if (!_refreshTokens.TryRemove(key, out var entry)) return null;
        if (entry.ExpiresAtUtc <= _time.GetUtcNow().UtcDateTime) return null;
        return Issue(entry.Username);
    }

    private TokenPair Issue(string username)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.JwtSecret)),
            SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Issuer,
            claims: [new Claim(ClaimTypes.Name, username), new Claim(JwtRegisteredClaimNames.Sub, username)],
            notBefore: now,
            expires: expires,
            signingCredentials: credentials);

        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        _refreshTokens[Hash(refresh)] = new RefreshEntry(username, now.AddDays(_options.RefreshTokenDays));

        return new TokenPair(new JwtSecurityTokenHandler().WriteToken(jwt), refresh, expires);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
