using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Environments;
using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Workflow;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentForge.Orchestrator;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registra o runner genérico, o ClaudeCodeAdapter e o launcher que decide ONDE os CLIs rodam (Agents:Launcher =
    /// "local" ou "host"). Novos CLIs: registrar outro ICliAgentAdapter.
    /// </summary>
    public static IServiceCollection AddAgentRunner(this IServiceCollection services, IConfiguration? configuration = null)
    {
        var agents = configuration?.GetSection(AgentsOptions.Section).Get<AgentsOptions>() ?? new AgentsOptions();
        var git = configuration?.GetSection(GitOptions.Section).Get<GitOptions>() ?? new GitOptions();
        services.TryAddSingleton(git);
        services.TryAddSingleton<IProcessLauncher>(_ => agents.CreateLauncher(git.SshDir));
        services.TryAddSingleton<IEnvironmentLaunchers>(sp => new EnvironmentLaunchers(
            sp.GetRequiredService<IProcessLauncher>(), sp.GetRequiredService<IServiceScopeFactory>(), sp.GetService<ISecretProtector>()));
        services.TryAddSingleton(sp => new GitCredentialService(sp.GetRequiredService<IEnvironmentLaunchers>(), sp.GetRequiredService<GitOptions>()));
        services.AddSingleton<ICliAgentAdapter, ClaudeCodeAdapter>();
        services.TryAddSingleton<AgentAdapterRegistry>();
        services.TryAddSingleton<IAgentEventPublisher, NullAgentEventPublisher>();
        services.TryAddSingleton<IUsageLimitTracker, UsageLimitTracker>();
        services.TryAddScoped<IAgentRunner>(sp => new CliAgentRunner(sp.GetRequiredService<AgentAdapterRegistry>(),
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<IAgentEventPublisher>(),
            sp.GetRequiredService<IEnvironmentLaunchers>(), sp.GetRequiredService<ILogger<CliAgentRunner>>(),
            sp.GetRequiredService<IUsageLimitTracker>()));
        services.TryAddSingleton(sp => new AgentCliService(sp.GetRequiredService<AgentAdapterRegistry>(),
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IEnvironmentLaunchers>(), sp.GetRequiredService<ILogger<AgentCliService>>()));
        return services;
    }

    /// <summary>Registra o orquestrador (BackgroundService), os processadores e as ações humanas. Requer AddAgentRunner e AddWorkspaces.</summary>
    public static IServiceCollection AddOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OrchestratorOptions>(configuration.GetSection(OrchestratorOptions.Section));
        services.AddSingleton<OrchestratorService>();
        services.AddSingleton<IWorkQueue>(sp => sp.GetRequiredService<OrchestratorService>());
        services.AddHostedService(sp => sp.GetRequiredService<OrchestratorService>());
        services.AddSingleton<IEnvironmentLoad>(sp => sp.GetRequiredService<OrchestratorService>());
        services.TryAddSingleton<IWorkflowEventPublisher, NullWorkflowEventPublisher>();
        services.AddSingleton<AgentExecutor>();
        services.AddScoped<WorkItemProcessor>();
        services.AddScoped<DemandProcessor>();
        services.AddScoped<WorkflowActions>();
        return services;
    }

    /// <summary>Registra o WorkspaceManager; a raiz vem de Workspaces:Root (padrão /data/repos).</summary>
    public static IServiceCollection AddWorkspaces(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkspaceOptions>(configuration.GetSection(WorkspaceOptions.Section));
        services.TryAddSingleton<IWorkspaceManager>(sp => new WorkspaceManager(
            sp.GetRequiredService<IOptions<WorkspaceOptions>>(), sp.GetRequiredService<IEnvironmentLaunchers>()));
        return services;
    }
}
