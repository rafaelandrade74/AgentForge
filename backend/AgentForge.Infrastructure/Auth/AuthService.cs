using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AgentForge.Infrastructure.Auth;

public record TokenPair(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc);

/// <summary>Hash BCrypt do administrador semeado por variável de ambiente; calculado uma vez por processo.</summary>
public class AdminCredentials(IOptions<AuthOptions> options)
{
    public string Username { get; } = options.Value.AdminUsername;
    public string PasswordHash { get; } = BCrypt.Net.BCrypt.HashPassword(options.Value.AdminPassword);
}

/// <summary>Login e renovação de tokens. Refresh tokens ficam no banco (só o hash) e são rotacionados a cada uso.</summary>
public class AuthService(IOptions<AuthOptions> options, AdminCredentials admin, AppDbContext db, TimeProvider time)
{
    private readonly AuthOptions _options = options.Value;

    public async Task<TokenPair?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        // Sempre verifica o hash, para não distinguir usuário inexistente por tempo de resposta.
        var passwordOk = BCrypt.Net.BCrypt.Verify(password, admin.PasswordHash);
        var userOk = string.Equals(username, admin.Username, StringComparison.Ordinal);
        return passwordOk && userOk ? await IssueAsync(admin.Username, ct) : null;
    }

    public async Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = Hash(refreshToken);
        var now = time.GetUtcNow().UtcDateTime;
        // Remoção atômica: um refresh token só pode ser usado uma vez, mesmo com requisições concorrentes.
        var entry = await db.RefreshTokens.Where(t => t.TokenHash == hash).FirstOrDefaultAsync(ct);
        if (entry is null) return null;
        var deleted = await db.RefreshTokens.Where(t => t.Id == entry.Id).ExecuteDeleteAsync(ct);
        if (deleted == 0 || entry.ExpiresAtUtc <= now) return null;
        return await IssueAsync(entry.Username, ct);
    }

    private async Task<TokenPair> IssueAsync(string username, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
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
        db.RefreshTokens.Add(new RefreshToken
        {
            TokenHash = Hash(refresh),
            Username = username,
            ExpiresAtUtc = now.AddDays(_options.RefreshTokenDays),
        });
        // Aproveita para limpar tokens vencidos.
        await db.RefreshTokens.Where(t => t.ExpiresAtUtc <= now).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);

        return new TokenPair(new JwtSecurityTokenHandler().WriteToken(jwt), refresh, expires);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
