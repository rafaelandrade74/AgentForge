using AgentForge.Orchestrator.Agents;
using AgentForge.Orchestrator.Workflow;
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

    /// <summary>Registra o orquestrador (BackgroundService), os processadores e as ações humanas. Requer AddAgentRunner e AddWorkspaces.</summary>
    public static IServiceCollection AddOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OrchestratorOptions>(configuration.GetSection(OrchestratorOptions.Section));
        services.AddSingleton<OrchestratorService>();
        services.AddSingleton<IWorkQueue>(sp => sp.GetRequiredService<OrchestratorService>());
        services.AddHostedService(sp => sp.GetRequiredService<OrchestratorService>());
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
        services.TryAddSingleton<IWorkspaceManager, WorkspaceManager>();
        return services;
    }
}
