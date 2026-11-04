using AgentForge.Execution;

namespace AgentForge.Orchestrator.Workspaces;

/// <summary>A mensagem passa por Secrets.MaskUrls: o stderr do git pode repetir a URL com o token embutido.</summary>
public class GitException(string message, int exitCode) : Exception(Secrets.MaskUrls(message))
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Chama o `git` sem shell (argumentos separados) e sem nunca pedir credenciais no terminal. Roda onde o launcher
/// manda: no container/computador da API ou, no modo "host", no computador pelo host agent.
/// </summary>
public static class GitCli
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    private static readonly LocalProcessLauncher Local = new();

    /// <summary>Roda o git neste computador/container (usado pelos testes e por quem não tem launcher).</summary>
    public static Task<string> RunAsync(string workingDirectory, IEnumerable<string> args, CancellationToken ct) =>
        RunAsync(Local, workingDirectory, args, ct);

    public static async Task<string> RunAsync(IProcessLauncher launcher, string workingDirectory, IEnumerable<string> args, CancellationToken ct)
    {
        var arguments = args.ToList();
        CompletedProcess result;
        try
        {
            result = await launcher.RunAsync(new LaunchSpec("git", arguments, workingDirectory), DefaultTimeout, ct);
        }
        catch (ProcessStartException ex)
        {
            throw new GitException($"Não foi possível iniciar o git: {ex.Message}", -1);
        }

        if (!result.Started) throw new GitException($"Não foi possível iniciar o git: {result.Stderr}", -1);
        if (result.TimedOut) throw new GitException($"git {arguments.FirstOrDefault()} excedeu o tempo limite.", -1);
        if (result.ExitCode != 0)
            throw new GitException($"git {string.Join(' ', arguments.Take(2))} falhou ({result.ExitCode}): {result.Stderr.Trim()}", result.ExitCode);
        return result.Stdout;
    }
}
