using System.Text;
using AgentForge.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.Section))
    .Validate(o => o.JwtSecret.Length >= 32, "Auth:JwtSecret deve ter ao menos 32 caracteres.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.AdminPassword), "Auth:AdminPassword é obrigatório.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AuthService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((jwt, auth) =>
    {
        var o = auth.Value;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = o.Issuer,
            ValidAudience = o.Issuer,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.JwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(10),
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

var auth = app.MapGroup("/api/auth");
auth.MapPost("/login", (LoginRequest req, AuthService svc) =>
    svc.Login(req.Username, req.Password) is { } tokens ? Results.Ok(tokens) : Results.Unauthorized());
auth.MapPost("/refresh", (RefreshRequest req, AuthService svc) =>
    svc.Refresh(req.RefreshToken) is { } tokens ? Results.Ok(tokens) : Results.Unauthorized());
auth.MapGet("/me", (HttpContext ctx) => Results.Ok(new { username = ctx.User.Identity!.Name }))
    .RequireAuthorization();

app.Run();

record LoginRequest(string Username, string Password);
record RefreshRequest(string RefreshToken);

public partial class Program;
