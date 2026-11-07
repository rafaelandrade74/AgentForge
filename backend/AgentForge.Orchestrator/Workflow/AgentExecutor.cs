using AgentForge.Domain;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Orchestrator.Workflow;

public enum ExecOutcome { Ok, Failed, Cancelled, Unavailable }

/// <summary>
/// A máquina do ambiente não respondeu: o passo não deve virar falha nem Blocked. O orquestrador solta o item, espera e tenta de
/// novo (backoff) até a máquina voltar; o estado persistido não muda, então nada se perde.
/// </summary>
public class EnvironmentDeferredException(string reason, UsageLimit? limit = null) : Exception(reason)
{
    /// <summary>Preenchido quando a espera é por limite de uso da conta do agente (e não por máquina fora do ar).</summary>
    public UsageLimit? Limit { get; } = limit;
}

/// <summary>Papel movido para outro ambiente que o do projeto (ver <see cref="ProjectRoleEnvironment"/>).</summary>
public record RoleEnvironment(Guid EnvironmentId);

public record ExecResult<T>(ExecOutcome Outcome, T? Value, string? Error, Guid? RunId = null, UsageLimit? Limit = null)
{
    public static ExecResult<T> Cancelled(Guid? runId = null) => new(ExecOutcome.Cancelled, default, null, runId);

    /// <exception cref="EnvironmentDeferredException">a máquina do ambiente está fora do ar.</exception>
    public ExecResult<T> ThrowIfUnavailable() =>
        Outcome == ExecOutcome.Unavailable ? throw new EnvironmentDeferredException(Error ?? "Ambiente indisponível.", Limit) : this;
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
        Func<string?, Task<Parsed<T>>> parse, CancellationToken ct, RoleEnvironment? roleEnvironment = null)
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
            var run = await runner.RunAsync(AgentRequest.FromConfig(config, text, workingDirectory, demandId, workItemId, projectId: projectId,
                environmentId: roleEnvironment?.EnvironmentId, environmentOverride: roleEnvironment is not null), ct);

            runId = run.RunId;
            if (run.Status == AgentRunStatus.Cancelled) return ExecResult<T>.Cancelled(runId);
            if (run.UsageLimit is { } limit) return new(ExecOutcome.Unavailable, default, Truncate(limit.Message, 500), runId, limit);   // espera liberar; não gasta a 2ª tentativa
            if (run.EnvironmentDown) return new(ExecOutcome.Unavailable, default, Truncate(run.Output, 500), runId);   // não gasta a 2ª tentativa
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
