using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using AgentForge.Execution;

namespace AgentForge.HostAgent;

public class HostAgentOptions
{
    public const string Section = "HostAgent";

    /// <summary>Segredo compartilhado com a API (Agents:HostAgent:Token). Mínimo de 24 caracteres.</summary>
    public string Token { get; set; } = "";

    /// <summary>Pastas onde processos podem rodar e onde o agente pode criar/apagar diretórios (os clones dos projetos).</summary>
    public List<string> Roots { get; set; } = [];

    /// <summary>Nome lógico -> caminho do binário. Só estes nomes podem ser executados. Padrão: claude e git pelo PATH.</summary>
    public Dictionary<string, string> Executables { get; set; } = new() { ["claude"] = "claude", ["git"] = "git" };
}

public sealed class PathGuard
{
    private readonly string[] _roots;
    private readonly StringComparison _comparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public PathGuard(IEnumerable<string> roots, string scratch)
    {
        Scratch = Full(scratch);
        _roots = roots.Select(Full).Append(Scratch).Distinct().ToArray();
        RootsOnly = _roots.Where(r => r != Scratch).ToArray();
    }

    public string Scratch { get; }
    public IReadOnlyList<string> RootsOnly { get; }

    private static string Full(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>true se o caminho está dentro de uma raiz permitida (ou é a própria raiz, se !strict).</summary>
    public bool Allows(string path, bool strict = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) return false;
        string full;
        try { full = Full(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        foreach (var root in _roots)
        {
            if (full.Equals(root, _comparison)) return !strict;
            if (full.StartsWith(root + Path.DirectorySeparatorChar, _comparison)) return true;
        }
        return false;
    }
}

/// <summary>Processos iniciados a pedido da API. Cada um tem um fluxo de eventos (stdout, stderr, saída).</summary>
public sealed class ProcessSessions(IProcessLauncher launcher, ILogger<ProcessSessions> logger)
{
    private sealed class Session
    {
        public required string Id { get; init; }
        public required IRunningProcess Process { get; init; }
        public Channel<ProcessEvent> Events { get; } = Channel.CreateUnbounded<ProcessEvent>();
        public int Streaming;
    }

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    public async Task<string> StartAsync(LaunchSpec spec, CancellationToken ct)
    {
        var process = await launcher.StartAsync(spec, ct);
        var session = new Session { Id = Guid.NewGuid().ToString("N"), Process = process };
        _sessions[session.Id] = session;

        var pumps = Task.WhenAll(Pump(session, process.ReadStdoutLineAsync, "out"), Pump(session, process.ReadStderrLineAsync, "err"));
        _ = Task.Run(async () =>
        {
            var exit = await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(5)));
            session.Events.Writer.TryWrite(new ProcessEvent("exit", C: exit));
            session.Events.Writer.TryComplete();
        });

        // quem inicia e nunca lê a saída não deixa processo órfão
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ =>
        {
            if (Volatile.Read(ref session.Streaming) == 0) Remove(session.Id, kill: true);
        });
        return session.Id;
    }

    private static async Task Pump(Session s, Func<CancellationToken, Task<string?>> read, string kind)
    {
        try
        {
            string? line;
            while ((line = await read(CancellationToken.None)) is not null)
                s.Events.Writer.TryWrite(new ProcessEvent(kind, line));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // processo encerrado
        }
    }

    /// <summary>Entrega os eventos de um processo. Só um leitor por processo; ao terminar (ou desconectar), limpa.</summary>
    public async IAsyncEnumerable<ProcessEvent> StreamAsync(string id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var session) || Interlocked.Exchange(ref session.Streaming, 1) == 1)
            yield break;

        try
        {
            await foreach (var evt in session.Events.Reader.ReadAllAsync(ct)) yield return evt;
        }
        finally
        {
            // desconexão da API (cancelamento, timeout, queda) mata o processo: nada fica rodando sem dono
            Remove(id, kill: true);
        }
    }

    public bool Exists(string id) => _sessions.ContainsKey(id);

    public async Task<bool> WriteStdinAsync(string id, string? text, bool close, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(id, out var session)) return false;
        if (text is not null) await session.Process.WriteStdinAsync(text, ct);
        if (close) await session.Process.CloseStdinAsync();
        return true;
    }

    public bool Remove(string id, bool kill)
    {
        if (!_sessions.TryRemove(id, out var session)) return false;
        if (kill) session.Process.Kill();
        _ = session.Process.DisposeAsync();
        logger.LogDebug("Processo {Id} removido (kill={Kill})", id, kill);
        return true;
    }
}
