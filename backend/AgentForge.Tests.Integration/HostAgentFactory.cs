using AgentForge.HostAgent;
using Microsoft.AspNetCore.Builder;

namespace AgentForge.Tests.Orchestrator;

public static class HostAgentFactory
{
    /// <summary>
    /// Host agent em porta aleatória, com `sh` liberado além de claude e git. <paramref name="sshDir"/>: pasta da chave SSH dele.
    /// <paramref name="docker"/>: habilita containers com um "docker" de mentira (script) e a pasta de login do Claude.
    /// </summary>
    public static WebApplication Create(string root, string token, string? sshDir = null, (string Executable, string ClaudeDir, string[] AllowedImages)? docker = null)
    {
        var args = new List<string>
        {
            "--urls=http://127.0.0.1:0",
            $"--HostAgent:Token={token}",
            $"--HostAgent:Roots:0={root}",
            "--HostAgent:Executables:sh=sh",
        };
        if (sshDir is not null) args.Add($"--HostAgent:SshDir={sshDir}");
        if (docker is { } d)
        {
            args.Add("--HostAgent:Docker:Enabled=true");
            args.Add($"--HostAgent:Docker:Executable={d.Executable}");
            args.Add($"--HostAgent:Docker:ClaudeConfigDir={d.ClaudeDir}");
            for (var i = 0; i < d.AllowedImages.Length; i++) args.Add($"--HostAgent:Docker:AllowedImages:{i}={d.AllowedImages[i]}");
        }
        return HostAgentApp.Build([.. args]);
    }
}
