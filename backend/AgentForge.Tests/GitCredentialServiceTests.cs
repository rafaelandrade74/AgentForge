using System.Diagnostics;
using AgentForge.Execution;
using AgentForge.Orchestrator.Workspaces;

namespace AgentForge.Tests;

public sealed class GitCredentialUnitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "af-ssh-unit-" + Guid.NewGuid().ToString("N"));
    private GitCredentialService Service(IProcessLauncher? launcher = null) =>
        new(launcher ?? new LocalProcessLauncher { GitSshDirectory = _dir }, new GitOptions { SshDir = _dir });

    private static string? SshEnv(ProcessStartInfo psi) => psi.Environment.TryGetValue("GIT_SSH_COMMAND", out var v) ? v : null;

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task In_host_agent_mode_the_feature_is_not_offered()
    {
        var remote = new RemoteProcessLauncher(new HttpClient(), "http://127.0.0.1:1", "token-qualquer-com-mais-de-24-chars");
        var service = Service(remote);

        var status = await service.GetStatusAsync(default);

        Assert.False(status.Supported);
        Assert.Contains("inacessível", status.Reason);
        await Assert.ThrowsAsync<GitCredentialException>(() => service.GenerateAsync(false, default));
        await Assert.ThrowsAsync<GitCredentialException>(() => service.DeleteAsync(default));
    }

    [Fact]
    public void Git_uses_the_generated_key_only_when_it_exists_and_only_for_git()
    {
        Directory.CreateDirectory(_dir);
        Assert.Null(SshEnv(LocalProcessLauncher.StartInfo("git", ["status"], _dir, _dir)));

        File.WriteAllText(Path.Combine(_dir, "id_ed25519"), "chave");
        var git = LocalProcessLauncher.StartInfo("git", ["status"], _dir, _dir).Environment["GIT_SSH_COMMAND"]!;

        Assert.Contains($"-i '{_dir.Replace('\\', '/')}/id_ed25519'", git);
        Assert.Contains("IdentitiesOnly=yes", git);
        Assert.Contains("StrictHostKeyChecking=accept-new", git);
        Assert.Contains("BatchMode=yes", git);
        Assert.Contains($"UserKnownHostsFile='{_dir.Replace('\\', '/')}/known_hosts'", git);
        Assert.Null(SshEnv(LocalProcessLauncher.StartInfo("claude", ["-p"], _dir, _dir)));
        Assert.Null(SshEnv(LocalProcessLauncher.StartInfo("git", ["status"], _dir)));
    }
}
