using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentForge.Execution;

/// <summary>Estado da chave SSH de uma máquina. Só a parte pública sai daqui; a privada nunca é lida nem devolvida.</summary>
public record SshKeyStatus(bool HasKey, string? PublicKey, string? Fingerprint, DateTime? CreatedAt, string Directory);

public class SshKeyException(string message) : Exception(message);

/// <summary>
/// Chave SSH ed25519 de UMA máquina, numa pasta própria do AgentForge (id_ed25519, id_ed25519.pub e known_hosts), nunca o
/// ~/.ssh do usuário. É usada pelo servidor (ambiente padrão) e pelo host agent (cada máquina cuida da própria chave: a
/// privada nasce e fica lá). O `git` iniciado por <see cref="LocalProcessLauncher"/> usa a chave se ela existir.
/// </summary>
public sealed class SshKeyStore(string directory)
{
    private const string KeyName = "id_ed25519";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Directory { get; } = directory;
    private string PrivateKeyPath => Path.Combine(Directory, KeyName);
    private string PublicKeyPath => PrivateKeyPath + ".pub";

    public async Task<SshKeyStatus> GetStatusAsync(CancellationToken ct)
    {
        if (!File.Exists(PrivateKeyPath) || !File.Exists(PublicKeyPath))
            return new SshKeyStatus(false, null, null, null, Directory);

        var publicKey = (await File.ReadAllTextAsync(PublicKeyPath, ct)).Trim();
        return new SshKeyStatus(true, publicKey, await FingerprintAsync(ct), File.GetCreationTimeUtc(PrivateKeyPath), Directory);
    }

    /// <summary>Gera a chave. Se já existe, só com overwrite = true (a antiga deixa de valer e precisa sair do GitHub).</summary>
    public async Task<SshKeyStatus> GenerateAsync(bool overwrite, CancellationToken ct)
    {
        var exists = File.Exists(PrivateKeyPath) || File.Exists(PublicKeyPath);
        if (exists && !overwrite)
            throw new SshKeyException("Já existe uma chave. Gere outra só se quiser substituí-la (a atual deixará de funcionar).");

        System.IO.Directory.CreateDirectory(Directory);
        TryRestrictDirectory(Directory);
        if (exists)
        {
            File.Delete(PrivateKeyPath);
            File.Delete(PublicKeyPath);
        }

        var comment = $"agentforge@{Environment.MachineName}";
        var result = await RunKeygenAsync(["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", PrivateKeyPath], ct);
        if (result is null)
            throw new SshKeyException("ssh-keygen não foi encontrado nesta máquina (instale o cliente OpenSSH).");
        if (result.ExitCode != 0 || !File.Exists(PublicKeyPath))
            throw new SshKeyException("Não foi possível gerar a chave: " + result.Stderr.Trim());

        TryRestrictFile(PrivateKeyPath);
        return await GetStatusAsync(ct);
    }

    public void Delete()
    {
        foreach (var path in new[] { PrivateKeyPath, PublicKeyPath }) if (File.Exists(path)) File.Delete(path);
    }

    private async Task<string?> FingerprintAsync(CancellationToken ct)
    {
        var result = await RunKeygenAsync(["-lf", PublicKeyPath], ct);
        // "256 SHA256:abc... agentforge@host (ED25519)"
        var parts = result?.Stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return result is { ExitCode: 0 } && parts.Length >= 2 ? parts[1] : null;
    }

    private async Task<CompletedProcess?> RunKeygenAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var launcher = new LocalProcessLauncher();
        var result = await launcher.RunAsync(new LaunchSpec("ssh-keygen", args, Directory), CommandTimeout, ct);
        return result.Started ? result : null;
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
