using AgentForge.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Infrastructure.Persistence;

/// <summary>
/// Perfis de stack prontos (Node 22, .NET 8, Python 3.12): um Dockerfile cada, construído na máquina do ambiente. Todos trazem
/// git, ssh, Node 20 e o Claude Code (versão fixa), porque o agente roda DENTRO do container. Recriados a cada start.
/// </summary>
public static class StackProfileSeeder
{
    public const string ClaudeCodeVersion = "2.1.316";

    private const string Env =
        "ENV CLAUDE_CONFIG_DIR=/agent-config HOME=/tmp/agent-home DISABLE_AUTOUPDATER=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1\n";

    private static string Install(string npmLinks) =>
        npmLinks +
        "RUN apt-get update \\\n" +
        "    && apt-get install -y --no-install-recommends git openssh-client ca-certificates curl sudo \\\n" +
        "    && rm -rf /var/lib/apt/lists/* \\\n" +
        $"    && npm install -g @anthropic-ai/claude-code@{ClaudeCodeVersion} \\\n" +
        "    && npm cache clean --force\n";

    /// <summary>Base que já traz o Node (imagem oficial do Node).</summary>
    private static string OnNode(string baseImage) => $"FROM {baseImage}\n" + Install("") + Env;

    /// <summary>Base sem Node (SDK do .NET, Python): copia o Node 20 oficial, como a imagem da API faz.</summary>
    private static string WithNode(string baseImage) =>
        "FROM node:20-bookworm-slim AS node\n" +
        $"FROM {baseImage}\n" +
        "COPY --from=node /usr/local/bin/node /usr/local/bin/node\n" +
        "COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules\n" +
        Install(
            "RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \\\n" +
            "    && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx\n") + Env;

    public static readonly (string Name, string Description, string Dockerfile)[] BuiltIns =
    [
        ("Node 22", "Node.js 22 com npm, git e o Claude Code.", OnNode("node:22-bookworm-slim")),
        (".NET 8", "SDK do .NET 8 com Node, git e o Claude Code.", WithNode("mcr.microsoft.com/dotnet/sdk:8.0")),
        ("Python 3.12", "Python 3.12 com pip, Node, git e o Claude Code.", WithNode("python:3.12-slim-bookworm")),
    ];

    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        var existing = await db.StackProfiles.Where(p => p.BuiltIn).ToListAsync(ct);
        foreach (var (name, description, dockerfile) in BuiltIns)
        {
            var profile = existing.FirstOrDefault(p => p.Name == name);
            if (profile is null)
            {
                if (await db.StackProfiles.AnyAsync(p => p.Name == name, ct)) continue;   // nome já tomado por um perfil do usuário
                db.StackProfiles.Add(new StackProfile
                {
                    Name = name, Description = description, Dockerfile = dockerfile, BuiltIn = true, CpuLimit = 2, MemoryMb = 4096,
                });
            }
            else
            {
                profile.Description = description;
                profile.Dockerfile = dockerfile;
                profile.Image = "";
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
