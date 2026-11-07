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
var sshKeys = new SshKeyStore(options.ResolvedSshDir());
builder.Services.AddSingleton(sshKeys);
builder.Services.AddSingleton<IProcessLauncher>(new LocalProcessLauncher
{
    Executables = allowedExecutables,
    GitSshDirectory = sshKeys.Directory,   // o git desta máquina usa a chave gerada pela API, quando existir
});
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
    guard.Scratch, guard.RootsOnly, allowedExecutables.Keys.Order().ToList(), options.Docker.Enabled));

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

// ---- containers por tarefa (perfis de stack): só com HostAgent:Docker:Enabled. O pedido nunca vira uma linha de docker livre:
// o host agent monta os argumentos (ContainerRules), aceita só imagens agentforge/* ou da lista, monta só a pasta do projeto,
// sem --privileged, sem socket do Docker e sem capacidades.
var docker = options.Docker;
var dockerLauncher = new LocalProcessLauncher();
var containerOfProcess = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
string? hostUser = null;
var isWindowsHost = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

app.Services.GetRequiredService<ProcessSessions>().OnRemoved = id =>
{
    // o processo local é o cliente do docker; matá-lo não derruba o container, então ele é removido à parte
    if (!containerOfProcess.TryRemove(id, out var name)) return;
    _ = dockerLauncher.RunAsync(new LaunchSpec(docker.Executable, ["rm", "-f", name], guard.Scratch), TimeSpan.FromSeconds(30), CancellationToken.None);
};

app.MapPost(HostAgentRoutes.Containers, async (StartContainerRequest req, ProcessSessions sessions, CancellationToken ct) =>
{
    if (!docker.Enabled)
        return Results.Json(new HostError("Containers não estão habilitados neste host agent (HostAgent:Docker:Enabled)."), statusCode: 403);
    var spec = req.Container;
    if (!ContainerRules.ImageAllowed(spec.Image, docker.AllowedImages))
        return Results.Json(new HostError($"Imagem '{spec.Image}' não permitida neste host agent (agentforge/* ou HostAgent:Docker:AllowedImages)."), statusCode: 403);
    if (!guard.Allows(spec.MountPath, strict: true) || !guard.Allows(req.WorkingDirectory))
        return Results.Json(new HostError("Pasta do projeto fora das pastas permitidas."), statusCode: 403);
    if (ContainerRules.Validate(spec, req.WorkingDirectory, isWindowsHost) is { } invalid)
        return Results.Json(new HostError(invalid), statusCode: 400);
    if (!Directory.Exists(req.WorkingDirectory))
        return Results.Json(new HostError($"Diretório de trabalho inexistente: {req.WorkingDirectory}"), statusCode: 400);

    var claudeDir = docker.ResolvedClaudeConfigDir();
    Directory.CreateDirectory(claudeDir);   // se não existir, o docker a criaria como root
    hostUser ??= (await Uid(dockerLauncher, guard.Scratch, "-u")) + ":" + (await Uid(dockerLauncher, guard.Scratch, "-g"));
    var name = "af-" + Guid.NewGuid().ToString("N")[..16];
    var args = ContainerRules.RunArguments(spec, name, req.WorkingDirectory, req.Executable, req.Arguments ?? [], claudeDir, hostUser,
        passApiKey: !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")));
    try
    {
        var id = await sessions.StartAsync(new LaunchSpec(docker.Executable, args, req.WorkingDirectory, req.Stdin, req.CloseStdin), ct);
        containerOfProcess[id] = name;
        return Results.Json(new StartProcessResponse(id), statusCode: 201);
    }
    catch (ProcessStartException ex)
    {
        return Results.Json(new HostError(ex.Message), statusCode: 422);
    }

    static async Task<string> Uid(LocalProcessLauncher launcher, string dir, string flag)
    {
        var r = await launcher.RunAsync(new LaunchSpec("id", [flag], dir), TimeSpan.FromSeconds(5), CancellationToken.None);
        return r.Started && r.ExitCode == 0 ? r.Stdout.Trim() : "1000";
    }
});

app.MapPost(HostAgentRoutes.ImagesEnsure, async (EnsureImageRequest req, CancellationToken ct) =>
{
    if (!docker.Enabled)
        return Results.Json(new HostError("Containers não estão habilitados neste host agent (HostAgent:Docker:Enabled)."), statusCode: 403);
    if (!ContainerRules.ImageAllowed(req.Image, docker.AllowedImages))
        return Results.Json(new HostError($"Imagem '{req.Image}' não permitida neste host agent."), statusCode: 403);
    var hasDockerfile = !string.IsNullOrWhiteSpace(req.Dockerfile);
    if (hasDockerfile && (!req.Image.StartsWith(ContainerRules.BuiltImagePrefix, StringComparison.Ordinal) || req.Dockerfile!.Length > 20000))
        return Results.Json(new HostError("Só é possível construir imagens agentforge/* (Dockerfile de até 20 mil caracteres)."), statusCode: 400);

    var inspect = await dockerLauncher.RunAsync(new LaunchSpec(docker.Executable, ["image", "inspect", req.Image], guard.Scratch), TimeSpan.FromSeconds(30), ct);
    if (!inspect.Started) return Results.Json(new HostError("Docker não encontrado nesta máquina."), statusCode: 422);
    var present = inspect.ExitCode == 0;
    if (!hasDockerfile || (present && !req.Force)) return Results.Ok(new EnsureImageResponse(req.Image, false, present, ""));

    // sem contexto de arquivos: o Dockerfile vem pelo stdin, então a construção não enxerga nada da máquina.
    // Sem flags opcionais (ex.: --progress): o docker.io das distros vem sem buildx e recusa o que não conhece.
    var build = await dockerLauncher.RunAsync(new LaunchSpec(docker.Executable, ["build", "-t", req.Image, "-"], guard.Scratch, req.Dockerfile),
        TimeSpan.FromMinutes(25), ct);
    var log = (build.Stdout + build.Stderr).Trim();
    if (log.Length > 8000) log = "…" + log[^8000..];
    return build.ExitCode == 0 && !build.TimedOut
        ? Results.Ok(new EnsureImageResponse(req.Image, true, true, log))
        : Results.Json(new HostError("Falha ao construir a imagem:\n" + log), statusCode: 422);
});

// Chave SSH da máquina: a privada nasce e fica aqui; só a pública (e a impressão digital) sai pela API.
app.MapGet(HostAgentRoutes.Ssh, (SshKeyStore keys, CancellationToken ct) => keys.GetStatusAsync(ct));

app.MapPost(HostAgentRoutes.SshGenerate, async (GenerateKeyRequest? req, SshKeyStore keys, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await keys.GenerateAsync(req?.Overwrite ?? false, ct));
    }
    catch (SshKeyException ex)
    {
        return Results.Json(new HostError(ex.Message), statusCode: 409);
    }
});

app.MapDelete(HostAgentRoutes.Ssh, async (SshKeyStore keys, CancellationToken ct) =>
{
    keys.Delete();
    return Results.Ok(await keys.GetStatusAsync(ct));
});

app.Logger.LogInformation("Host agent; pastas permitidas: {Roots}; executáveis: {Exe}",
    string.Join(", ", guard.RootsOnly), string.Join(", ", allowedExecutables.Keys));
return app;

static IResult Forbidden() => Results.Json(new HostError("Caminho fora das pastas permitidas."), statusCode: 403);
    }
}
