using System.Diagnostics;
using System.Text;

namespace AgentForge.Orchestrator.Workspaces;

public class GitException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Chama o binário `git` sem shell (argumentos em ArgumentList) e sem nunca pedir credenciais no terminal.</summary>
public static class GitCli
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    public static async Task<string> RunAsync(string workingDirectory, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";

        using var timeout = new CancellationTokenSource(DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        using var process = Process.Start(psi) ?? throw new GitException("Não foi possível iniciar o git.", -1);
        using var kill = linked.Token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        var (output, error) = (await stdout, await stderr);

        ct.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested) throw new GitException($"git {args.FirstOrDefault()} excedeu o tempo limite.", -1);
        if (process.ExitCode != 0)
            throw new GitException($"git {string.Join(' ', args.Take(2))} falhou ({process.ExitCode}): {error.Trim()}", process.ExitCode);
        return output;
    }
}
