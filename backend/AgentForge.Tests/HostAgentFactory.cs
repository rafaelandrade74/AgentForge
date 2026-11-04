using AgentForge.HostAgent;
using Microsoft.AspNetCore.Builder;

namespace AgentForge.Tests.Orchestrator;

public static class HostAgentFactory
{
    /// <summary>Host agent em porta aleatória, com `sh` liberado além de claude e git. <paramref name="sshDir"/>: pasta da chave SSH dele.</summary>
    public static WebApplication Create(string root, string token, string? sshDir = null)
    {
        var args = new List<string>
        {
            "--urls=http://127.0.0.1:0",
            $"--HostAgent:Token={token}",
            $"--HostAgent:Roots:0={root}",
            "--HostAgent:Executables:sh=sh",
        };
        if (sshDir is not null) args.Add($"--HostAgent:SshDir={sshDir}");
        return HostAgentApp.Build([.. args]);
    }
}
