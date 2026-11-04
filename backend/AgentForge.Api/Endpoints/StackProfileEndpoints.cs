using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Environments;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Api.Endpoints;

public record StackProfileInput(string? Name, string? Description, string? Image, string? Dockerfile, string? Network, double CpuLimit = 0, int MemoryMb = 0);

public record StackProfileDto(Guid Id, string Name, string Description, string Image, string Dockerfile, string Network, double CpuLimit, int MemoryMb,
    bool BuiltIn, int ProjectCount, DateTime CreatedAt);

public record BuildImageResult(bool Ok, string Image, bool Built, string Log);

public static class StackProfileEndpoints
{
    public static void MapStackProfileEndpoints(this IEndpointRouteBuilder app)
    {
        // só com o login (JWT): perfis definem o que roda em container nas máquinas; um token do MCP não os altera
        var g = app.MapGroup("/api/stack-profiles").RequireAuthorization().WithTags("StackProfiles");

        g.MapGet("/", async (AppDbContext db) =>
        {
            var counts = await db.Projects.Where(p => p.StackProfileId != null)
                .GroupBy(p => p.StackProfileId!.Value).Select(x => new { Id = x.Key, N = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.N);
            return (await db.StackProfiles.AsNoTracking().OrderByDescending(p => p.BuiltIn).ThenBy(p => p.Name).ToListAsync())
                .Select(p => ToDto(p, counts.GetValueOrDefault(p.Id)));
        }).WithSummary("Perfis de stack (prontos e do usuário).");

        g.MapPost("/", async (StackProfileInput input, AppDbContext db) =>
        {
            if (Validate(input) is { } bad) return bad;
            var name = input.Name!.Trim();
            if (await db.StackProfiles.AnyAsync(p => p.Name == name)) return Results.Conflict(new { error = $"Já existe um perfil chamado '{name}'." });
            var profile = new StackProfile();
            Apply(profile, input);
            db.StackProfiles.Add(profile);
            await db.SaveChangesAsync();
            return Results.Created($"/api/stack-profiles/{profile.Id}", ToDto(profile, 0));
        });

        g.MapPut("/{id:guid}", async (Guid id, StackProfileInput input, AppDbContext db) =>
        {
            if (Validate(input) is { } bad) return bad;
            if (await db.StackProfiles.FindAsync(id) is not { } profile) return Results.NotFound();
            if (profile.BuiltIn) return Results.Conflict(new { error = "Perfis prontos não podem ser editados: crie um perfil seu a partir dele." });
            var name = input.Name!.Trim();
            if (await db.StackProfiles.AnyAsync(p => p.Name == name && p.Id != id)) return Results.Conflict(new { error = $"Já existe um perfil chamado '{name}'." });
            Apply(profile, input);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(profile, await db.Projects.CountAsync(p => p.StackProfileId == id)));
        });

        g.MapDelete("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            if (await db.StackProfiles.FindAsync(id) is not { } profile) return Results.NotFound();
            if (profile.BuiltIn) return Results.Conflict(new { error = "Perfis prontos não podem ser apagados." });
            if (await db.Projects.CountAsync(p => p.StackProfileId == id) is var used and > 0)
                return Results.Conflict(new { error = $"O perfil está em uso por {used} projeto(s). Mude o perfil deles antes de apagar." });
            db.StackProfiles.Remove(profile);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // constrói (ou reconstrói, com force) a imagem do perfil NA MÁQUINA do ambiente, devolvendo o log
        g.MapPost("/{id:guid}/build", async (Guid id, Guid environmentId, bool? force, AppDbContext db, IEnvironmentLaunchers environments, CancellationToken ct) =>
        {
            if (await db.StackProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is not { } profile) return Results.NotFound();
            if (await db.ExecutionEnvironments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == environmentId, ct) is not { } environment)
                return Results.NotFound(new { error = "Ambiente não encontrado." });
            try
            {
                var target = environments.ForEnvironment(environment);
                if (target.Launcher is not RemoteProcessLauncher remote) return Results.Conflict(new { error = "Só ambientes com host agent constroem imagens." });
                var hasDockerfile = !string.IsNullOrWhiteSpace(profile.Dockerfile);
                var image = hasDockerfile ? ContainerRules.ImageTag(profile.Dockerfile) : profile.Image;
                var result = await remote.EnsureImageAsync(image, hasDockerfile ? profile.Dockerfile : null, force ?? false, ct);
                return Results.Ok(new BuildImageResult(true, result.Image, result.Built, result.Log));
            }
            catch (Exception ex) when (ex is ProcessStartException or EnvironmentUnavailableException)
            {
                return Results.Ok(new BuildImageResult(false, profile.Image, false, ex.Message));
            }
        }).WithSummary("Garante a imagem do perfil na máquina do ambiente (constrói do Dockerfile se faltar ou com force).");
    }

    internal static StackProfileDto ToDto(StackProfile p, int projectCount) =>
        new(p.Id, p.Name, p.Description, p.Image, p.Dockerfile, p.Network, p.CpuLimit, p.MemoryMb, p.BuiltIn, projectCount, p.CreatedAt);

    private static void Apply(StackProfile p, StackProfileInput i)
    {
        p.Name = i.Name!.Trim();
        p.Description = (i.Description ?? "").Trim();
        p.Image = (i.Image ?? "").Trim();
        var dockerfile = (i.Dockerfile ?? "").Replace("\r\n", "\n").Trim();
        p.Dockerfile = dockerfile.Length > 0 ? dockerfile + "\n" : "";
        p.Network = i.Network ?? "bridge";
        p.CpuLimit = i.CpuLimit;
        p.MemoryMb = i.MemoryMb;
    }

    private static IResult? Validate(StackProfileInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return Invalid("name", "Informe o nome.");
        if (i.Name.Trim().Length > 100) return Invalid("name", "O nome pode ter até 100 caracteres.");
        var hasImage = !string.IsNullOrWhiteSpace(i.Image);
        var hasDockerfile = !string.IsNullOrWhiteSpace(i.Dockerfile);
        if (hasImage == hasDockerfile) return Invalid("image", "Informe a imagem OU o Dockerfile (um dos dois).");
        if (hasImage && !ContainerRules.IsValidImageName(i.Image!.Trim())) return Invalid("image", "Nome de imagem inválido (ex.: node:22-bookworm ou minha/imagem:1).");
        if (hasDockerfile && i.Dockerfile!.Length > 20000) return Invalid("dockerfile", "O Dockerfile pode ter até 20 mil caracteres.");
        if ((i.Network ?? "bridge") is not ("bridge" or "none")) return Invalid("network", "Rede: \"bridge\" (com rede) ou \"none\" (sem rede).");
        if (i.CpuLimit is < 0 or > 128) return Invalid("cpuLimit", "Limite de CPU entre 0 (sem limite) e 128.");
        if (i.MemoryMb is < 0 or > 1_048_576) return Invalid("memoryMb", "Limite de memória em MB (0 = sem limite).");
        return null;
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
