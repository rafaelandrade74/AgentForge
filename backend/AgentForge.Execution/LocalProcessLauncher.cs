using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AgentForge.Execution;

/// <summary>Executa no próprio computador/container onde a aplicação roda.</summary>
public sealed class LocalProcessLauncher : IProcessLauncher
{
    /// <summary>
    /// Mapa opcional nome lógico -> caminho do binário (ex.: "claude" -> "C:\\...\\claude.exe"). Usado pelo host agent;
    /// sem entrada, o nome é resolvido pelo PATH.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Executables { get; init; }

    /// <summary>
    /// Pasta da chave SSH gerada pelo AgentForge. Quando existe um id_ed25519 nela, todo `git` iniciado por este launcher usa
    /// essa chave (e o known_hosts da pasta), sem tocar no ~/.ssh do usuário.
    /// </summary>
    public string? GitSshDirectory { get; init; }

    public string Mode => "local";
    public string Description => "neste computador/container";

    public Task<string> ScratchDirectoryAsync(CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentforge-scratch");
        Directory.CreateDirectory(dir);
        return Task.FromResult(dir);
    }

    public Task<IRunningProcess> StartAsync(LaunchSpec spec, CancellationToken ct)
    {
        if (spec.Container is not null)
            throw new ProcessStartException("Container por tarefa só funciona em ambiente com host agent com Docker (tela Ambientes).");
        var executable = Executables is not null && Executables.TryGetValue(spec.Executable, out var mapped) ? mapped : spec.Executable;
        Process process;
        try
        {
            process = Process.Start(StartInfo(executable, spec.Arguments, spec.WorkingDirectory, GitSshDirectory))
                      ?? throw new ProcessStartException($"Não foi possível iniciar '{spec.Executable}'.");
        }
        catch (Win32Exception ex)
        {
            throw new ProcessStartException($"Não foi possível iniciar '{spec.Executable}': {ex.Message}");
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new ProcessStartException($"Diretório de trabalho inexistente: {ex.Message}");
        }

        var running = new LocalRunningProcess(process);
        if (spec.Stdin is not null) running.WriteInitial(spec.Stdin, spec.CloseStdin);
        else if (spec.CloseStdin) running.CloseInitial();
        return Task.FromResult<IRunningProcess>(running);
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct) => Task.FromResult(Directory.Exists(path));

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string path, CancellationToken ct)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        return Task.CompletedTask;
    }

    public static ProcessStartInfo StartInfo(string executable, IEnumerable<string> args, string workingDirectory, string? gitSshDirectory = null)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        Secrets.ScrubEnvironment(psi.Environment);
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "git", StringComparison.OrdinalIgnoreCase))
        {
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";   // nunca fica preso pedindo credencial
            psi.Environment["LC_ALL"] = "C";                // mensagens previsíveis
            if (gitSshDirectory is not null && GitSshCommand(gitSshDirectory) is { } ssh) psi.Environment["GIT_SSH_COMMAND"] = ssh;
        }
        // Compose repassa ANTHROPIC_API_KEY="" quando a chave não foi definida; um valor vazio não deve chegar ao CLI
        // (ele poderia tratá-lo como "chave definida" e não usar o login salvo).
        foreach (var key in psi.Environment.Where(e => e.Key.StartsWith("ANTHROPIC_", StringComparison.Ordinal) && string.IsNullOrEmpty(e.Value)).Select(e => e.Key).ToList())
            psi.Environment.Remove(key);
        return psi;
    }

    /// <summary>O comando ssh que o git deve usar com a chave do AgentForge; null se não há chave gerada (ou a pasta é inválida).</summary>
    public static string? GitSshCommand(string sshDirectory)
    {
        var dir = sshDirectory.Replace('\\', '/').TrimEnd('/');
        if (dir.Contains('\'') || !File.Exists(Path.Combine(sshDirectory, "id_ed25519"))) return null;
        // accept-new: confia no host na primeira vez e depois exige a mesma chave (fica no known_hosts da pasta)
        return $"ssh -i '{dir}/id_ed25519' -o IdentitiesOnly=yes -o UserKnownHostsFile='{dir}/known_hosts' " +
               "-o StrictHostKeyChecking=accept-new -o BatchMode=yes";
    }

    private sealed class LocalRunningProcess(Process process) : IRunningProcess
    {
        public void WriteInitial(string text, bool close)
        {
            try
            {
                process.StandardInput.Write(text);
                if (close) process.StandardInput.Close();
                else process.StandardInput.Flush();
            }
            catch (IOException)
            {
                // o processo terminou antes de ler tudo
            }
        }

        public void CloseInitial()
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }

        public async Task WriteStdinAsync(string text, CancellationToken ct = default)
        {
            try
            {
                await process.StandardInput.WriteAsync(text.AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
            }
            catch (IOException)
            {
                // o processo terminou
            }
        }

        public Task CloseStdinAsync()
        {
            CloseInitial();
            return Task.CompletedTask;
        }

        public async Task<string?> ReadStdoutLineAsync(CancellationToken ct) => await process.StandardOutput.ReadLineAsync(ct);
        public async Task<string?> ReadStderrLineAsync(CancellationToken ct) => await process.StandardError.ReadLineAsync(ct);

        public async Task<int> WaitForExitAsync(CancellationToken ct)
        {
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }

        public void Kill()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // já terminou
            }
        }

        public ValueTask DisposeAsync()
        {
            Kill();
            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
