using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentForge.Execution;

/// <summary>Onde (e com que limites) um processo roda num container do perfil de stack. Só host agent com Docker.</summary>
/// <param name="Image">Imagem efetiva (já com a tag calculada quando o perfil tem Dockerfile).</param>
/// <param name="Dockerfile">Conteúdo para construir a imagem na máquina quando ela não existe; null para imagem pronta.</param>
/// <param name="Network">"bridge" ou "none".</param>
/// <param name="MountPath">Pasta do projeto na máquina (clone + worktrees): a única coisa montada, no mesmo caminho dentro do container.</param>
public record ContainerSpec(string Image, string? Dockerfile, string Network, double CpuLimit, int MemoryMb, string MountPath);

/// <summary>Regras puras (testáveis) do que o host agent aceita rodar em container e dos argumentos do `docker run`.</summary>
public static class ContainerRules
{
    public const string BuiltImagePrefix = "agentforge/";
    private static readonly Regex ImageName = new(@"^[a-z0-9][a-z0-9._/:@-]{0,199}$", RegexOptions.Compiled);

    /// <summary>Tag da imagem construída de um Dockerfile: muda quando o conteúdo muda, então editar o perfil reconstrói.</summary>
    public static string ImageTag(string dockerfile) =>
        BuiltImagePrefix + "stack:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dockerfile.Replace("\r\n", "\n"))))[..12].ToLowerInvariant();

    public static bool IsValidImageName(string image) => ImageName.IsMatch(image ?? "");

    /// <summary>Imagens construídas pelo AgentForge sempre valem; as demais precisam estar na lista do host agent (exata ou com `*` no fim).</summary>
    public static bool ImageAllowed(string image, IEnumerable<string> allowed)
    {
        if (!IsValidImageName(image)) return false;
        if (image.StartsWith(BuiltImagePrefix, StringComparison.Ordinal)) return true;
        foreach (var rule in allowed)
        {
            var r = rule.Trim();
            if (r.Length == 0) continue;
            if (r.EndsWith('*') ? image.StartsWith(r[..^1], StringComparison.Ordinal) : image == r) return true;
        }
        return false;
    }

    /// <summary>Mensagem de erro se o pedido não pode rodar; null se está tudo certo.</summary>
    public static string? Validate(ContainerSpec spec, string workingDirectory, bool isWindowsHost)
    {
        if (isWindowsHost)
            return "Containers por tarefa exigem host agent em Linux ou macOS (o worktree é montado no mesmo caminho dentro do container).";
        if (spec.Network is not ("bridge" or "none")) return "Rede inválida: use \"bridge\" ou \"none\".";
        if (spec.CpuLimit is < 0 or > 128 || double.IsNaN(spec.CpuLimit)) return "Limite de CPU inválido.";
        if (spec.MemoryMb is < 0 or > 1_048_576) return "Limite de memória inválido.";
        var mount = spec.MountPath.TrimEnd('/');
        if (!mount.StartsWith('/') || mount.Contains("..") || mount.Contains(':') || mount.Contains(',') || mount.Length < 2)
            return "Pasta do projeto inválida para montar no container.";
        var wd = workingDirectory.TrimEnd('/');
        if (wd != mount && !wd.StartsWith(mount + "/", StringComparison.Ordinal))
            return "O diretório de trabalho precisa estar dentro da pasta montada do projeto.";
        return null;
    }

    /// <summary>
    /// Argumentos do `docker run`: sem --privileged, sem socket do Docker, sem capacidades, só a pasta do projeto (e o login do
    /// agente) montados, limites de CPU/memória/processos, usuário do dono da pasta (nunca root).
    /// </summary>
    public static List<string> RunArguments(ContainerSpec spec, string containerName, string workingDirectory, string executable,
        IReadOnlyList<string> arguments, string claudeConfigDir, string? user, bool passApiKey)
    {
        var mount = spec.MountPath.TrimEnd('/');
        var args = new List<string>
        {
            "run", "--rm", "-i", "--name", containerName,
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--pids-limit", "1024",
            "--network", spec.Network,
        };
        if (spec.CpuLimit > 0) args.AddRange(["--cpus", spec.CpuLimit.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)]);
        if (spec.MemoryMb > 0) args.AddRange(["--memory", spec.MemoryMb + "m"]);
        if (user is not null) args.AddRange(["--user", user]);
        args.AddRange(["-v", $"{mount}:{mount}", "-v", $"{claudeConfigDir}:/agent-config", "-w", workingDirectory,
            "-e", "HOME=/tmp/agent-home", "-e", "CLAUDE_CONFIG_DIR=/agent-config"]);
        if (passApiKey) args.AddRange(["-e", "ANTHROPIC_API_KEY"]);   // sem valor: o docker copia do ambiente do host agent
        args.Add(spec.Image);
        args.Add(executable);
        args.AddRange(arguments);
        return args;
    }
}
