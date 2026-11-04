using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Orchestrator.Environments;

/// <summary>Protege segredos guardados no banco (o token de cada host agent). A implementação real usa o Data Protection do ASP.NET.</summary>
public interface ISecretProtector
{
    string Protect(string plain);

    /// <exception cref="CryptographicException">texto inválido ou chave que já não existe.</exception>
    string Unprotect(string protectedText);
}

/// <summary>Onde um projeto roda: o launcher (quem executa os processos) e a raiz dos clones, ambos do lado dessa máquina.</summary>
/// <param name="WorkspaceRoot">Null = a raiz de Workspaces:Root (ambiente padrão).</param>
public sealed record EnvironmentTarget(IProcessLauncher Launcher, string? WorkspaceRoot, Guid? EnvironmentId, string Name);

/// <summary>O ambiente do projeto não existe mais ou não tem token legível: a execução não pode começar.</summary>
public class EnvironmentUnavailableException(string message) : Exception(message);

public interface IEnvironmentLaunchers
{
    /// <summary>O ambiente padrão do servidor (Agents:Launcher), usado por projetos sem ambiente próprio.</summary>
    EnvironmentTarget Default { get; }

    /// <exception cref="EnvironmentUnavailableException"/>
    Task<EnvironmentTarget> ResolveAsync(Guid? environmentId, CancellationToken ct);

    /// <summary>Alvo de um ambiente já carregado (ex.: o teste de conexão).</summary>
    EnvironmentTarget ForEnvironment(ExecutionEnvironment environment);
}

public static class EnvironmentLauncherExtensions
{
    public static Task<EnvironmentTarget> ForProjectAsync(this IEnvironmentLaunchers environments, Project project, CancellationToken ct) =>
        environments.ResolveAsync(project.EnvironmentId, ct);
}

/// <summary>Um único ambiente para tudo (testes e instalações sem ambientes cadastrados).</summary>
public sealed class SingleEnvironment(IProcessLauncher launcher) : IEnvironmentLaunchers
{
    public EnvironmentTarget Default { get; } = new(launcher, null, null, "Padrão");
    public Task<EnvironmentTarget> ResolveAsync(Guid? environmentId, CancellationToken ct) => Task.FromResult(Default);
    public EnvironmentTarget ForEnvironment(ExecutionEnvironment environment) => Default;
}

/// <summary>
/// Entrega o launcher do ambiente de cada projeto. Ambientes cadastrados viram um <see cref="RemoteProcessLauncher"/>
/// (reaproveitado enquanto URL e token não mudam); sem ambiente, vale o launcher padrão do servidor.
/// </summary>
public sealed class EnvironmentLaunchers(IProcessLauncher defaultLauncher, IServiceScopeFactory scopes, ISecretProtector? protector)
    : IEnvironmentLaunchers
{
    private readonly ConcurrentDictionary<string, RemoteProcessLauncher> _remotes = new();

    public EnvironmentTarget Default { get; } = new(defaultLauncher, null, null, "Padrão");

    public async Task<EnvironmentTarget> ResolveAsync(Guid? environmentId, CancellationToken ct)
    {
        if (environmentId is null) return Default;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var environment = await db.ExecutionEnvironments.AsNoTracking().FirstOrDefaultAsync(e => e.Id == environmentId, ct)
            ?? throw new EnvironmentUnavailableException("O ambiente deste projeto não existe mais. Escolha outro ambiente no projeto.");
        return ForEnvironment(environment);
    }

    public EnvironmentTarget ForEnvironment(ExecutionEnvironment environment)
    {
        string token;
        try
        {
            token = (protector ?? throw new CryptographicException("sem protetor de segredos")).Unprotect(environment.ProtectedToken);
        }
        catch (CryptographicException)
        {
            throw new EnvironmentUnavailableException(
                $"Não foi possível ler o token do ambiente '{environment.Name}' (a chave de proteção mudou?). Edite o ambiente e informe o token de novo.");
        }

        var key = $"{environment.Id}|{environment.Url}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))}";
        if (!_remotes.TryGetValue(key, out var launcher))
        {
            foreach (var old in _remotes.Keys.Where(k => k.StartsWith(environment.Id + "|", StringComparison.Ordinal)))
                _remotes.TryRemove(old, out _);
            launcher = _remotes.GetOrAdd(key, _ => new RemoteProcessLauncher(new HttpClient(), environment.Url, token));
        }
        return new EnvironmentTarget(launcher, environment.WorkspaceRoot, environment.Id, environment.Name);
    }
}
