using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Orchestrator.Workflow;

public enum ExecOutcome { Ok, Failed, Cancelled }

public record ExecResult<T>(ExecOutcome Outcome, T? Value, string? Error, Guid? RunId = null)
{
    public static ExecResult<T> Cancelled(Guid? runId = null) => new(ExecOutcome.Cancelled, default, null, runId);
}

/// <summary>
/// Roda um papel de agente e valida o JSON final. Saída inválida (ou execução com falha) conta como falha e é
/// reexecutada UMA vez, com o motivo anexado ao prompt. Cada execução usa um escopo próprio (DbContext próprio),
/// então revisões em paralelo não compartilham contexto.
/// </summary>
public class AgentExecutor(IServiceScopeFactory scopes)
{
    public async Task<ExecResult<T>> RunAsync<T>(
        AgentRole role, string prompt, string workingDirectory, Guid projectId, Guid? demandId, Guid? workItemId,
        Func<string?, Task<Parsed<T>>> parse, CancellationToken ct)
    {
        AgentRoleConfig? config;
        using (var scope = scopes.CreateScope())
            config = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .AgentRoleConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Role == role, ct);
        if (config is null) return new(ExecOutcome.Failed, default, $"Não há configuração para o papel {role}.");

        string? lastError = null;
        Guid? runId = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var text = attempt == 0
                ? prompt
                : $"{prompt}\n\n---\nA tentativa anterior falhou: {lastError}\nRefaça o trabalho e termine com o bloco JSON no formato exigido.";

            using var scope = scopes.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
            var run = await runner.RunAsync(AgentRequest.FromConfig(config, text, workingDirectory, demandId, workItemId, projectId: projectId), ct);

            runId = run.RunId;
            if (run.Status == AgentRunStatus.Cancelled) return ExecResult<T>.Cancelled(runId);
            if (run.Status == AgentRunStatus.Succeeded)
            {
                var parsed = await parse(run.Json);
                if (parsed.Ok) return new(ExecOutcome.Ok, parsed.Value, null, runId);
                lastError = parsed.Error;
            }
            else
            {
                lastError = "a execução falhou: " + Truncate(run.Output, 500);
            }
        }
        return new(ExecOutcome.Failed, default, lastError, runId);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
