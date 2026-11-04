using System.Runtime.InteropServices;
using AgentForge.Execution;

namespace AgentForge.Orchestrator.Workspaces;

public class GitOptions
{
    public const string Section = "Git";

    /// <summary>
    /// Pasta da chave SSH que o AgentForge gera (id_ed25519, id_ed25519.pub e known_hosts). No Docker é o volume
    /// montado em /home/app/.ssh; fora dele, uma pasta própria do AgentForge (nunca o ~/.ssh do usuário).
    /// </summary>
    public string SshDir { get; set; } = DefaultSshDir();

    public static string DefaultSshDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(string.IsNullOrEmpty(home) ? Path.GetTempPath() : home, ".agentforge", "ssh");
    }
}

public record GitSshStatus(
    bool Supported, string? Reason, bool HasKey, string? PublicKey, string? Fingerprint, DateTime? CreatedAt, string Directory);

public record GitAccessResult(bool Ok, string Message);

public class GitCredentialException(string message) : Exception(message);

/// <summary>
/// Credenciais Git do AgentForge para repositórios privados por SSH: gera uma chave ed25519 (a privada nunca sai do
/// servidor; só a pública é mostrada, para colar no GitHub/GitLab) e testa o acesso a um repositório. Só vale quando o
/// git roda aqui ("local"); no modo host agent o git é o do computador, com as credenciais dele.
/// </summary>
public sealed class GitCredentialService(IProcessLauncher launcher, GitOptions options)
{
    private const string KeyName = "id_ed25519";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private string Dir => options.SshDir;
    private string PrivateKeyPath => Path.Combine(Dir, KeyName);
    private string PublicKeyPath => PrivateKeyPath + ".pub";
    private bool Supported => launcher.Mode == "local";

    private const string HostReason =
        "O git roda no seu computador (host agent): ele usa as credenciais que você já configurou lá (chave SSH, credential manager).";

    public async Task<GitSshStatus> GetStatusAsync(CancellationToken ct)
    {
        if (!Supported) return new GitSshStatus(false, HostReason, false, null, null, null, Dir);
        if (!File.Exists(PrivateKeyPath) || !File.Exists(PublicKeyPath))
            return new GitSshStatus(true, null, false, null, null, null, Dir);

        var publicKey = (await File.ReadAllTextAsync(PublicKeyPath, ct)).Trim();
        return new GitSshStatus(true, null, true, publicKey, await FingerprintAsync(ct), File.GetCreationTimeUtc(PrivateKeyPath), Dir);
    }

    /// <summary>Gera a chave. Se já existe, só com overwrite = true (a antiga deixa de valer e precisa sair do GitHub).</summary>
    public async Task<GitSshStatus> GenerateAsync(bool overwrite, CancellationToken ct)
    {
        if (!Supported) throw new GitCredentialException(HostReason);
        var exists = File.Exists(PrivateKeyPath) || File.Exists(PublicKeyPath);
        if (exists && !overwrite)
            throw new GitCredentialException("Já existe uma chave. Gere outra só se quiser substituí-la (a atual deixará de funcionar).");

        Directory.CreateDirectory(Dir);
        TryRestrictDirectory(Dir);
        if (exists)
        {
            File.Delete(PrivateKeyPath);
            File.Delete(PublicKeyPath);
        }

        var comment = $"agentforge@{Environment.MachineName}";
        var result = await launcher.RunAsync(
            new LaunchSpec("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", PrivateKeyPath], Dir),
            CommandTimeout, ct);
        if (!result.Started)
            throw new GitCredentialException("ssh-keygen não foi encontrado neste servidor (instale o cliente OpenSSH).");
        if (result.ExitCode != 0 || !File.Exists(PublicKeyPath))
            throw new GitCredentialException("Não foi possível gerar a chave: " + result.Stderr.Trim());

        TryRestrictFile(PrivateKeyPath);
        return await GetStatusAsync(ct);
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        if (!Supported) throw new GitCredentialException(HostReason);
        foreach (var path in new[] { PrivateKeyPath, PublicKeyPath }) if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    /// <summary>Testa se a chave (ou as credenciais do git) alcançam o repositório: `git ls-remote --heads`.</summary>
    public async Task<GitAccessResult> TestAccessAsync(string repositoryUrl, CancellationToken ct)
    {
        var url = (repositoryUrl ?? "").Trim();
        if (url.Length == 0 || url.Length > 500 || url.StartsWith('-') || url.Any(char.IsWhiteSpace) || url.Any(char.IsControl))
            return new GitAccessResult(false, "Informe a URL do repositório (ex.: git@github.com:organizacao/repositorio.git).");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout + TimeSpan.FromSeconds(10));
        try
        {
            var scratch = await launcher.ScratchDirectoryAsync(timeout.Token);
            var output = await GitCli.RunAsync(launcher, scratch, ["ls-remote", "--heads", "--", url], timeout.Token);
            var branches = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            return new GitAccessResult(true, branches == 0
                ? "Acesso confirmado (o repositório ainda não tem branches)."
                : branches == 1 ? "Acesso confirmado (1 branch visível)." : $"Acesso confirmado ({branches} branches visíveis).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new GitAccessResult(false, "Tempo esgotado ao falar com o servidor Git. Confira a URL e a rede.");
        }
        catch (GitException ex)
        {
            return new GitAccessResult(false, Explain(ex.Message));
        }
    }

    private async Task<string?> FingerprintAsync(CancellationToken ct)
    {
        var result = await launcher.RunAsync(new LaunchSpec("ssh-keygen", ["-lf", PublicKeyPath], Dir), CommandTimeout, ct);
        // "256 SHA256:abc... agentforge@host (ED25519)"
        var parts = result.Stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return result.ExitCode == 0 && parts.Length >= 2 ? parts[1] : null;
    }

    private static string Explain(string gitMessage)
    {
        var m = gitMessage;
        if (m.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) || m.Contains("publickey", StringComparison.OrdinalIgnoreCase))
            return "A chave não tem acesso a este repositório. Adicione a chave pública no GitHub (Deploy key do repositório ou chave SSH da conta) e teste de novo.";
        if (m.Contains("Could not resolve host", StringComparison.OrdinalIgnoreCase))
            return "Não foi possível resolver o endereço do servidor Git. Confira a URL e a rede do servidor.";
        if (m.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            return "A verificação da chave do servidor falhou (known_hosts). Remova a entrada antiga do host e teste de novo.";
        if (m.Contains("not found", StringComparison.OrdinalIgnoreCase) || m.Contains("does not appear to be a git repository", StringComparison.OrdinalIgnoreCase))
            return "Repositório não encontrado (ou sem acesso). Confira a URL e as permissões.";
        return m;
    }

    // permissões estritas onde existem (Linux/macOS): o ssh recusa chave privada legível por outros
    private static void TryRestrictDirectory(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    private static void TryRestrictFile(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }
}
