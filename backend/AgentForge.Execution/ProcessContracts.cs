namespace AgentForge.Execution;

/// <summary>O que executar: sempre um binário com argumentos separados (nunca uma linha de shell).</summary>
public record LaunchSpec(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    /// <summary>Texto enviado ao stdin logo ao iniciar (ex.: o prompt).</summary>
    string? Stdin = null,
    /// <summary>Fecha o stdin depois de enviar <see cref="Stdin"/>; false para fluxos interativos (login).</summary>
    bool CloseStdin = true,
    /// <summary>Se definido, roda neste container (só host agent com Docker); null = direto na máquina.</summary>
    ContainerSpec? Container = null);

public class ProcessStartException(string message) : Exception(message);

/// <summary>A máquina do host agent não respondeu (desligada, sem rede). Diferente de recusa/erro: vale tentar de novo quando ela voltar.</summary>
public class HostUnreachableException(string message) : ProcessStartException(message);

/// <summary>Um processo em execução, local ou no computador hospedeiro. Leituras devolvem null no fim do fluxo.</summary>
public interface IRunningProcess : IAsyncDisposable
{
    Task WriteStdinAsync(string text, CancellationToken ct = default);
    Task CloseStdinAsync();
    Task<string?> ReadStdoutLineAsync(CancellationToken ct);
    Task<string?> ReadStderrLineAsync(CancellationToken ct);
    Task<int> WaitForExitAsync(CancellationToken ct);
    /// <summary>Mata o processo e os filhos.</summary>
    void Kill();
}

/// <summary>
/// Onde os CLIs (agentes e git) rodam: neste processo/container (<c>local</c>) ou no computador hospedeiro, por um
/// host agent (<c>host</c>). Os caminhos que a aplicação passa são sempre caminhos DO LADO ONDE O PROCESSO RODA.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>"local" ou "host".</summary>
    string Mode { get; }
    string Description { get; }

    /// <summary>Pasta temporária de trabalho para execuções avulsas (testes de conexão, status).</summary>
    Task<string> ScratchDirectoryAsync(CancellationToken ct);

    /// <exception cref="ProcessStartException">binário ausente ou recusado.</exception>
    Task<IRunningProcess> StartAsync(LaunchSpec spec, CancellationToken ct);

    Task<bool> DirectoryExistsAsync(string path, CancellationToken ct);
    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task DeleteDirectoryAsync(string path, CancellationToken ct);

    /// <summary>Grava um arquivo de texto (UTF-8, quebras LF), criando a pasta se preciso; <paramref name="executable"/> = chmod +x (no-op no Windows). No host agent só vale dentro de     Task DeleteDirectoryAsync(string path, CancellationToken ct);lt;clone&gt;/.git/hooks.</summary>
    Task WriteFileAsync(string path, string content, bool executable, CancellationToken ct);
}

/// <param name="Unreachable">Não iniciou porque a máquina do host agent não respondeu (vale tentar de novo depois).</param>
public record CompletedProcess(int ExitCode, string Stdout, string Stderr, bool Started, bool TimedOut, bool Unreachable = false);

public static class ProcessLauncherExtensions
{
    /// <summary>Roda um comando curto até o fim (status, versão, logout, git). Binário ausente vira Started = false.</summary>
    public static async Task<CompletedProcess> RunAsync(this IProcessLauncher launcher, LaunchSpec spec, TimeSpan timeout, CancellationToken ct)
    {
        IRunningProcess process;
        try
        {
            process = await launcher.StartAsync(spec, ct);
        }
        catch (ProcessStartException ex)
        {
            return new CompletedProcess(-1, "", ex.Message, Started: false, TimedOut: false, Unreachable: ex is HostUnreachableException);
        }

        await using (process)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var kill = linked.Token.Register(process.Kill);

            var stdout = ReadAllAsync(process.ReadStdoutLineAsync);
            var stderr = ReadAllAsync(process.ReadStderrLineAsync);
            var exitCode = await process.WaitForExitAsync(CancellationToken.None);
            var (o, e) = (await stdout, await stderr);

            ct.ThrowIfCancellationRequested();
            return new CompletedProcess(exitCode, o, e, Started: true, TimedOut: timeoutCts.IsCancellationRequested);
        }
    }

    private static async Task<string> ReadAllAsync(Func<CancellationToken, Task<string?>> readLine)
    {
        var lines = new List<string>();
        string? line;
        while ((line = await readLine(CancellationToken.None)) is not null) lines.Add(line);
        return string.Join('\n', lines) + (lines.Count > 0 ? "\n" : "");
    }
}
