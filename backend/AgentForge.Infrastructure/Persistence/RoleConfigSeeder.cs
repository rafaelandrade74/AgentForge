using AgentForge.Domain;
using Microsoft.EntityFrameworkCore;

namespace AgentForge.Infrastructure.Persistence;

/// <summary>Cria a configuração padrão dos 5 papéis que ainda não existem, lendo prompts/&lt;papel&gt;.md.</summary>
public static class RoleConfigSeeder
{
    private static readonly (AgentRole Role, string File, string Tools)[] Defaults =
    [
        (AgentRole.ScrumMaster, "scrum-master", "Read,Glob,Grep"),
        (AgentRole.Dev, "dev", "Read,Write,Edit,Bash"),
        (AgentRole.TechLead, "tech-lead", "Read,Glob,Grep,Bash"),
        (AgentRole.Security, "security", "Read,Grep,Bash"),
        (AgentRole.Qa, "qa", "Read,Write,Bash"),
    ];

    public static async Task SeedAsync(AppDbContext db, string promptsDir, CancellationToken ct = default)
    {
        var existing = await db.AgentRoleConfigs.Select(c => c.Role).ToListAsync(ct);
        foreach (var (role, file, tools) in Defaults.Where(d => !existing.Contains(d.Role)))
        {
            var path = Path.Combine(promptsDir, file + ".md");
            db.AgentRoleConfigs.Add(new AgentRoleConfig
            {
                Role = role,
                AdapterName = "claude-code",
                SystemPrompt = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : $"Você é o papel {role}.",
                AllowedTools = tools,
                Model = "sonnet",
                MaxTurns = 20,
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
