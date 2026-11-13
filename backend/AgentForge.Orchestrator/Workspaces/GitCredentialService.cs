using AgentForge.Execution;
using AgentForge.Orchestrator.Environments;

namespace AgentForge.Orchestrator.Workspaces;

public class GitOptions
{
    public const string Section = "Git";

    /// <summary>
    /// Pasta da chave SSH que o AgentForge gera (id_ed25519, id_ed25519.pub e known_hosts). No Docker é o volume
    /// montado em /home/app/.agentforge/ssh; fora dele, ~/.agentforge/ssh (nunca o ~/.ssh do usuário).
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
/// Credenciais Git do AgentForge para repositórios privados por SSH, POR AMBIENTE: gera uma chave ed25519 na máquina onde
/// o git roda (a privada nunca sai dela; só a pública é mostrada, para colar no GitHub/GitLab) e testa o acesso a um
/// repositório. No ambiente padrão local a chave fica na pasta <see cref="GitOptions.SshDir"/>; numa máquina com host
/// agent, o próprio host agent gera e guarda a chave dele. Cada ambiente tem a sua, então um não enxerga a do outro.
/// </summary>
public sealed class GitCredentialService(IEnvironmentLaunchers environments, GitOptions options)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Um ambiente só, com o launcher dado (testes e instalações sem ambientes cadastrados).</summary>
    public GitCredentialService(IProcessLauncher launcher, GitOptions options) : this(new SingleEnvironment(launcher), options) { }

    private const string UnsupportedReason =
        "O git deste ambiente roda fora do AgentForge: ele usa as credenciais que já estão configuradas lá (chave SSH, credential manager).";

    // ------------------------------------------------------------------ escolha do ambiente

    /// <summary>Gerenciador de chave do ambiente: o do servidor (pasta local) ou o do host agent. Null = não suportado.</summary>
    private interface IKeys
    {
        Task<SshKeyStatus> StatusAsync(CancellationToken ct);
        Task<SshKeyStatus> GenerateAsync(bool overwrite, CancellationToken ct);
        Task<SshKeyStatus> DeleteAsync(CancellationToken ct);
    }

    private sealed class LocalKeys(SshKeyStore store) : IKeys
    {
        public Task<SshKeyStatus> StatusAsync(CancellationToken ct) => store.GetStatusAsync(ct);
        public Task<SshKeyStatus> GenerateAsync(bool overwrite, CancellationToken ct) => store.GenerateAsync(overwrite, ct);
        public async Task<SshKeyStatus> DeleteAsync(CancellationToken ct)
        {
            store.Delete();
            return await store.GetStatusAsync(ct);
        }
    }

    private sealed class RemoteKeys(RemoteProcessLauncher remote) : IKeys
    {
        public Task<SshKeyStatus> StatusAsync(CancellationToken ct) => remote.SshStatusAsync(ct);
        public Task<SshKeyStatus> GenerateAsync(bool overwrite, CancellationToken ct) => remote.SshGenerateAsync(overwrite, ct);
        public Task<SshKeyStatus> DeleteAsync(CancellationToken ct) => remote.SshDeleteAsync(ct);
    }

    private async Task<(EnvironmentTarget Target, IKeys? Keys)> ResolveAsync(Guid? environmentId, CancellationToken ct)
    {
        EnvironmentTarget target;
        try
        {
            target = await environments.ResolveAsync(environmentId, ct);
        }
        catch (EnvironmentUnavailableException ex)
        {
            throw new GitCredentialException(ex.Message);
        }

        IKeys? keys = target.Launcher switch
        {
            RemoteProcessLauncher remote => new RemoteKeys(remote),
            { Mode: "local" } => new LocalKeys(new SshKeyStore(options.SshDir)),
            _ => null,
        };
        return (target, keys);
    }

    private static async Task<T> TranslateAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (SshKeyException ex)
        {
            throw new GitCredentialException(ex.Message);
        }
        catch (ProcessStartException ex)
        {
            throw new GitCredentialException(ex.Message);
        }
    }

    private static GitSshStatus ToStatus(SshKeyStatus s) =>
        new(true, null, s.HasKey, s.PublicKey, s.Fingerprint, s.CreatedAt, s.Directory);

    // ------------------------------------------------------------------ operações

    public Task<GitSshStatus> GetStatusAsync(CancellationToken ct) => GetStatusAsync(null, ct);

    /// <summary>Não lança por host agent fora do ar nem versão antiga: devolve "não suportado" com o motivo, para a tela mostrar.</summary>
    public async Task<GitSshStatus> GetStatusAsync(Guid? environmentId, CancellationToken ct)
    {
        var (_, keys) = await ResolveAsync(environmentId, ct);
        if (keys is null) return new GitSshStatus(false, UnsupportedReason, false, null, null, null, "");
        try
        {
            return ToStatus(await keys.StatusAsync(ct));
        }
        catch (Exception ex) when (ex is SshKeyException or ProcessStartException)
        {
            return new GitSshStatus(false, ex.Message, false, null, null, null, "");
        }
    }

    public Task<GitSshStatus> GenerateAsync(bool overwrite, CancellationToken ct) => GenerateAsync(null, overwrite, ct);

    /// <summary>Gera a chave. Se já existe, só com overwrite = true (a antiga deixa de valer e precisa sair do GitHub).</summary>
    public async Task<GitSshStatus> GenerateAsync(Guid? environmentId, bool overwrite, CancellationToken ct)
    {
        var (_, keys) = await ResolveAsync(environmentId, ct);
        if (keys is null) throw new GitCredentialException(UnsupportedReason);
        return ToStatus(await TranslateAsync(() => keys.GenerateAsync(overwrite, ct)));
    }

    public Task DeleteAsync(CancellationToken ct) => DeleteAsync(null, ct);

    public async Task DeleteAsync(Guid? environmentId, CancellationToken ct)
    {
        var (_, keys) = await ResolveAsync(environmentId, ct);
        if (keys is null) throw new GitCredentialException(UnsupportedReason);
        await TranslateAsync(() => keys.DeleteAsync(ct));
    }

    public Task<GitAccessResult> TestAccessAsync(string repositoryUrl, CancellationToken ct) => TestAccessAsync(null, repositoryUrl, ct);

    /// <summary>Testa se a chave (ou as credenciais do git) do ambiente alcançam o repositório: `git ls-remote --heads`.</summary>
    public async Task<GitAccessResult> TestAccessAsync(Guid? environmentId, string repositoryUrl, CancellationToken ct)
    {
        var url = (repositoryUrl ?? "").Trim();
        if (url.Length == 0 || url.Length > 500 || url.StartsWith('-') || url.Any(char.IsWhiteSpace) || url.Any(char.IsControl))
            return new GitAccessResult(false, "Informe a URL do repositório (ex.: git@github.com:organizacao/repositorio.git).");

        var (target, _) = await ResolveAsync(environmentId, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout + TimeSpan.FromSeconds(10));
        try
        {
            var scratch = await target.Launcher.ScratchDirectoryAsync(timeout.Token);
            var output = await GitCli.RunAsync(target.Launcher, scratch, ["ls-remote", "--heads", "--", url], timeout.Token);
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
        catch (ProcessStartException ex)
        {
            return new GitAccessResult(false, ex.Message);
        }
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
}
