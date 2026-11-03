using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Workspaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentForge.Orchestrator;

public static class ServiceCollectionExtensions
{
    /// <summary>Registra o runner genérico e o ClaudeCodeAdapter. Novos CLIs: registrar outro ICliAgentAdapter.</summary>
    public static IServiceCollection AddAgentRunner(this IServiceCollection services)
    {
        services.AddSingleton<ICliAgentAdapter, ClaudeCodeAdapter>();
        services.TryAddSingleton<AgentAdapterRegistry>();
        services.TryAddSingleton<IAgentEventPublisher, NullAgentEventPublisher>();
        services.TryAddScoped<IAgentRunner, CliAgentRunner>();
        return services;
    }

    /// <summary>Registra o WorkspaceManager; a raiz vem de Workspaces:Root (padrão /data/repos).</summary>
    public static IServiceCollection AddWorkspaces(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkspaceOptions>(configuration.GetSection(WorkspaceOptions.Section));
        services.TryAddSingleton<IWorkspaceManager, WorkspaceManager>();
        return services;
    }
}
