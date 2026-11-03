using System.Text;
using System.Text.Json.Serialization;
using AgentForge.Api.Endpoints;
using AgentForge.Api.Realtime;
using AgentForge.Infrastructure.Auth;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Workflow;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

builder.Services.AddDbContext<AppDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")));

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "AgentForge API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header,
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
});

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.Section))
    .Validate(o => o.JwtSecret.Length >= 32, "Auth:JwtSecret deve ter ao menos 32 caracteres.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.AdminPassword), "Auth:AdminPassword é obrigatório.")
    .ValidateOnStart();
builder.Services.AddAgentRunner();
builder.Services.AddWorkspaces(builder.Configuration);
builder.Services.AddOrchestrator(builder.Configuration);

// Tempo real: troca os publicadores nulos pelos que transmitem via SignalR.
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.Replace(ServiceDescriptor.Singleton<IAgentEventPublisher, SignalRAgentEventPublisher>());
builder.Services.Replace(ServiceDescriptor.Singleton<IWorkflowEventPublisher, SignalRWorkflowEventPublisher>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AdminCredentials>();
builder.Services.AddScoped<AuthService>();

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
        // WebSockets não enviam cabeçalhos: o cliente SignalR passa o JWT em ?access_token=, aceito só em /hubs.
        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Migrations aplicam sozinhas no start; depois semeia a configuração padrão dos papéis.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    var promptsDir = app.Configuration["Prompts:Directory"] ?? Path.Combine(app.Environment.ContentRootPath, "prompts");
    await RoleConfigSeeder.SeedAsync(db, promptsDir);
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

var auth = app.MapGroup("/api/auth").WithTags("Auth");
auth.MapPost("/login", async (LoginRequest req, AuthService svc, CancellationToken ct) =>
    await svc.LoginAsync(req.Username, req.Password, ct) is { } tokens ? Results.Ok(tokens) : Results.Unauthorized());
auth.MapPost("/refresh", async (RefreshRequest req, AuthService svc, CancellationToken ct) =>
    await svc.RefreshAsync(req.RefreshToken, ct) is { } tokens ? Results.Ok(tokens) : Results.Unauthorized());
auth.MapGet("/me", (HttpContext ctx) => Results.Ok(new { username = ctx.User.Identity!.Name }))
    .RequireAuthorization();

app.MapCrudEndpoints();
app.MapWorkflowEndpoints();
app.MapHub<RunsHub>("/hubs/runs");

app.Run();

record LoginRequest(string Username, string Password);
record RefreshRequest(string RefreshToken);

public partial class Program;
