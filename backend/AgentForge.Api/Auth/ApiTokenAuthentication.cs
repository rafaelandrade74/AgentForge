using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentForge.Api.Auth;

public static class ApiTokens
{
    public const string Scheme = "ApiToken";
    public const string Prefix = "afk_";

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Token novo: o valor completo só existe nesta resposta; o banco guarda o hash.</summary>
    public static (string Token, ApiToken Entity) Create(string name)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = Prefix + secret;
        return (token, new ApiToken { Name = name.Trim(), TokenHash = Hash(token), Prefix = token[..(Prefix.Length + 4)] });
    }
}

/// <summary>
/// Autentica "Authorization: Bearer afk_..." contra os tokens de acesso (hash no banco, revogáveis). Esses tokens só são
/// aceitos nas rotas que listam este esquema (o endpoint /mcp); o resto da API continua exigindo o JWT do login.
/// </summary>
public class ApiTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer " + ApiTokens.Prefix, StringComparison.Ordinal)) return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        var db = Context.RequestServices.GetRequiredService<AppDbContext>();
        var hash = ApiTokens.Hash(token);
        var entry = await db.ApiTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null);
        if (entry is null) return AuthenticateResult.Fail("Token inválido ou revogado.");

        var now = time.GetUtcNow().UtcDateTime;
        if (entry.LastUsedAt is null || now - entry.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            entry.LastUsedAt = now;
            await db.SaveChangesAsync();
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "token:" + entry.Name), new Claim("token_id", entry.Id.ToString())], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

public record CreateTokenRequest(string? Name);
public record TokenDto(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

public static class TokenEndpoints
{
    public static void MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        // exige o login (JWT): um token de acesso não cria nem lista tokens
        var g = app.MapGroup("/api/tokens").RequireAuthorization().WithTags("Tokens");

        g.MapGet("/", async (AppDbContext db) =>
            (await db.ApiTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync())
            .Select(t => new TokenDto(t.Id, t.Name, t.Prefix, t.CreatedAt, t.LastUsedAt, t.RevokedAt)));

        g.MapPost("/", async (CreateTokenRequest req, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Informe um nome de até 100 caracteres."] });
            var (token, entity) = ApiTokens.Create(req.Name);
            db.ApiTokens.Add(entity);
            await db.SaveChangesAsync();
            // o valor completo aparece só aqui
            return Results.Created($"/api/tokens/{entity.Id}", new { id = entity.Id, name = entity.Name, prefix = entity.Prefix, token });
        }).WithSummary("Cria um token de acesso (valor exibido uma única vez). Vale apenas em /mcp.");

        g.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, TimeProvider time) =>
            await db.ApiTokens.Where(t => t.Id == id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, time.GetUtcNow().UtcDateTime)) > 0
                ? Results.NoContent() : Results.NotFound());
    }
}
