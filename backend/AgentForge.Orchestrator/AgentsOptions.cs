using AgentForge.Execution;

namespace AgentForge.Orchestrator;

public class AgentsOptions
{
    public const string Section = "Agents";

    /// <summary>"local": os CLIs rodam neste processo/container. "host": rodam no computador, por um host agent.</summary>
    public string Launcher { get; set; } = "local";

    public HostAgentOptions HostAgent { get; set; } = new();

    public IProcessLauncher CreateLauncher(string? gitSshDirectory = null) => Launcher.Trim().ToLowerInvariant() switch
    {
        "local" or "" => new LocalProcessLauncher { GitSshDirectory = gitSshDirectory },
        "host" => HostAgent.Url.Length == 0 || HostAgent.Token.Length == 0
            ? throw new InvalidOperationException("Agents:Launcher=host exige Agents:HostAgent:Url e Agents:HostAgent:Token.")
            : new RemoteProcessLauncher(new HttpClient(), HostAgent.Url, HostAgent.Token),
        var other => throw new InvalidOperationException($"Agents:Launcher '{other}' inválido: use 'local' ou 'host'."),
    };
}

public class HostAgentOptions
{
    /// <summary>Endereço do host agent visto pela API (do container: http://host.docker.internal:8787).</summary>
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
}
