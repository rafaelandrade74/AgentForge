using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Execution;

namespace AgentForge.HostAgent;

/// <summary>
/// Host agent: deixa a API (no Docker) usar o claude e o git que já estão instalados e logados NESTE computador.
/// Só executa os binários permitidos, só dentro das pastas permitidas e só para quem tem o token.
/// </summary>
public static class HostAgentApp
{
    public static WebApplication Build(string[] args)
    {
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://127.0.0.1:8787");

var options = builder.Configuration.GetSection(HostAgentOptions.Section).Get<HostAgentOptions>() ?? new HostAgentOptions();
if (options.Token.Length < 24)
    throw new InvalidOperationException("HostAgent:Token deve ter ao menos 24 caracteres (o mesmo valor de Agents:HostAgent:Token da API).");
if (options.Roots.Count == 0)
    throw new InvalidOperationException("Informe ao menos uma pasta de trabalho em HostAgent:Roots (ex.: --HostAgent:Roots:0=C:/agentforge/repos).");
foreach (var root in options.Roots) Directory.CreateDirectory(root);

var scratch = Path.Combine(Path.GetTempPath(), "agentforge-scratch");
Directory.CreateDirectory(scratch);
var guard = new PathGuard(options.Roots, scratch);
var allowedExecutables = new Dictionary<string, string>(options.Executables, StringComparer.Ordinal);

builder.Services.AddSingleton(guard);
builder.Services.AddSingleton<IProcessLauncher>(new LocalProcessLauncher { Executables = allowedExecutables });
builder.Services.AddSingleton<ProcessSessions>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();

var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.Token));
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/v1"))
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), tokenHash))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new HostError("Token inválido."));
            return;
        }
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet(HostAgentRoutes.Info, () => new HostInfo(
    RuntimeInformation.OSDescription,
    typeof(HostAgentOptions).Assembly.GetName().Version?.ToString() ?? "1",
    guard.Scratch, guard.RootsOnly, allowedExecutables.Keys.Order().ToList()));

app.MapPost(HostAgentRoutes.Processes, async (StartProcessRequest req, ProcessSessions sessions, CancellationToken ct) =>
{
    if (!allowedExecutables.ContainsKey(req.Executable))
        return Results.Json(new HostError($"Executável '{req.Executable}' não é permitido neste host agent."), statusCode: 403);
    if (!guard.Allows(req.WorkingDirectory))
        return Results.Json(new HostError("Diretório de trabalho fora das pastas permitidas."), statusCode: 403);
    if (!Directory.Exists(req.WorkingDirectory))
        return Results.Json(new HostError($"Diretório de trabalho inexistente: {req.WorkingDirectory}"), statusCode: 400);

    try
    {
        var id = await sessions.StartAsync(new LaunchSpec(req.Executable, req.Arguments ?? [], req.WorkingDirectory, req.Stdin, req.CloseStdin), ct);
        return Results.Json(new StartProcessResponse(id), statusCode: 201);
    }
    catch (ProcessStartException ex)
    {
        return Results.Json(new HostError(ex.Message), statusCode: 422);
    }
});

app.MapGet("/v1/processes/{id}/events", async (string id, HttpContext ctx, ProcessSessions sessions) =>
{
    if (!sessions.Exists(id))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new HostError("Processo não encontrado (ou já sendo lido)."));
        return;
    }

    ctx.Response.ContentType = "application/x-ndjson";
    ctx.Response.Headers.CacheControl = "no-store";
    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);   // headers saem já, para o cliente anexar ao fluxo
    try
    {
        await foreach (var evt in sessions.StreamAsync(id, ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(evt, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // a API desconectou: StreamAsync já matou o processo
    }
});

app.MapPost("/v1/processes/{id}/stdin", async (string id, StdinRequest req, ProcessSessions sessions, CancellationToken ct) =>
    await sessions.WriteStdinAsync(id, req.Text, req.Close, ct) ? Results.NoContent() : Results.NotFound(new HostError("Processo não encontrado.")));

app.MapDelete("/v1/processes/{id}", (string id, ProcessSessions sessions) =>
    sessions.Remove(id, kill: true) ? Results.NoContent() : Results.NotFound(new HostError("Processo não encontrado.")));

app.MapPost(HostAgentRoutes.FsExists, (PathRequest req) =>
    guard.Allows(req.Path) ? Results.Ok(new ExistsResponse(Directory.Exists(req.Path))) : Forbidden());

app.MapPost(HostAgentRoutes.FsMkdir, (PathRequest req) =>
{
    if (!guard.Allows(req.Path)) return Forbidden();
    Directory.CreateDirectory(req.Path);
    return Results.NoContent();
});

app.MapPost(HostAgentRoutes.FsDelete, (PathRequest req) =>
{
    if (!guard.Allows(req.Path, strict: true)) return Forbidden();   // nunca a própria raiz
    if (Directory.Exists(req.Path))
    {
        // objetos do git são somente leitura no Windows
        foreach (var file in Directory.EnumerateFiles(req.Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(req.Path, recursive: true);
    }
    return Results.NoContent();
});

app.Logger.LogInformation("Host agent; pastas permitidas: {Roots}; executáveis: {Exe}",
    string.Join(", ", guard.RootsOnly), string.Join(", ", allowedExecutables.Keys));
return app;

static IResult Forbidden() => Results.Json(new HostError("Caminho fora das pastas permitidas."), statusCode: 403);
    }
}
