namespace AgentForge.Infrastructure.Auth;

public class AuthOptions
{
    public const string Section = "Auth";

    public string JwtSecret { get; set; } = "";
    public string Issuer { get; set; } = "agentforge";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 7;
    public string AdminUsername { get; set; } = "admin";
    public string AdminPassword { get; set; } = "";
}
