using AgentForge.HostAgent;
using Microsoft.AspNetCore.Builder;

namespace AgentForge.Tests.Orchestrator;

public static class HostAgentFactory
{
    /// <summary>Host agent em porta aleatória, com `sh` liberado além de claude e git.</summary>
    public static WebApplication Create(string root, string token) =>
        HostAgentApp.Build([
            "--urls=http://127.0.0.1:0",
            $"--HostAgent:Token={token}",
            $"--HostAgent:Roots:0={root}",
            "--HostAgent:Executables:sh=sh",
        ]);
}
