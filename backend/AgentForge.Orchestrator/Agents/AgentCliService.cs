using System.Collections.Concurrent;
using System.Diagnostics;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentForge.Orchestrator.Agents;

public class AgentNotFoundException(string name) : Exception($"Agente '{name}' não existe.");
public class AgentNotSupportedException(string message) : Exception(message);

/// <summary>
/// Tela "Agentes": diz se cada CLI está instalado e conectado, testa a conexão de verdade e conduz o login por link
/// (o CLI imprime o link e espera o código; a pessoa autoriza no navegador e cola o código aqui).
/// </summary>
public sealed class AgentCliService(
    AgentAdapterRegistry adapters, IServiceScopeFactory scopes, IProcessLauncher launcher, ILogger<AgentCliService> logger)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LoginLinkTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LoginCodeTimeout = TimeSpan.FromSeconds(40);
    public static TimeSpan LoginIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
    private const int MaxCodeLength = 600;

    private sealed record Probe(bool Installed, string? Version, AgentAuthState Auth, DateTime At);

    private readonly ConcurrentDictionary<string, Probe> _probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentTestResult> _tests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LoginSession> _logins = new();

    // ------------------------------------------------------------------ status

    /// <summary>Onde os CLIs rodam: no container/computador da API ("local") ou no computador, pelo host agent ("host").</summary>
    public (string Mode, string Description) Environment => (launcher.Mode, launcher.Description);

    private async Task<CompletedProcess> QuickAsync(string executable, IReadOnlyList<string> args, CancellationToken ct) =>
        await launcher.RunAsync(new LaunchSpec(executable, args, await ScratchAsync(ct)), ProbeTimeout, ct);

    private async Task<string> ScratchAsync(CancellationToken ct)
    {
        try
        {
            return await launcher.ScratchDirectoryAsync(ct);
        }
        catch (ProcessStartException)
        {
            return Path.GetTempPath();   // o erro de verdade aparece ao tentar iniciar o processo
        }
    }

    public async Task<IReadOnlyList<AgentInfo>> ListAsync(CancellationToken ct)
    {
        Dictionary<string, List<string>> usage;
        using (var scope = scopes.CreateScope())
        {
            var configs = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .AgentRoleConfigs.AsNoTracking().Select(c => new { c.Role, c.AdapterName }).ToListAsync(ct);
            usage = configs.GroupBy(c => c.AdapterName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Role.ToString()).ToList(), StringComparer.OrdinalIgnoreCase);
        }

        var list = adapters.All.ToList();
        var infos = await Task.WhenAll(list.Select(a => InfoAsync(a, usage.GetValueOrDefault(a.Name) ?? [], force: false, ct)));
        return infos;
    }

    public async Task<AgentInfo> GetAsync(string name, bool force, CancellationToken ct)
    {
        var adapter = adapters.TryGet(name) ?? throw new AgentNotFoundException(name);
        return await InfoAsync(adapter, [], force, ct);
    }

    private async Task<AgentInfo> InfoAsync(ICliAgentAdapter adapter, IReadOnlyList<string> roles, bool force, CancellationToken ct)
    {
        var account = adapter as ICliAgentAccount;
        var probe = await ProbeAsync(adapter, account, force, ct);
        _tests.TryGetValue(adapter.Name, out var lastTest);
        return new AgentInfo(adapter.Name, account?.DisplayName ?? adapter.Name, probe.Installed, probe.Version,
            probe.Auth.Authenticated, probe.Auth.Method, probe.Auth.Detail, account?.LoginArguments is not null, roles, lastTest);
    }

    private async Task<Probe> ProbeAsync(ICliAgentAdapter adapter, ICliAgentAccount? account, bool force, CancellationToken ct)
    {
        if (!force && _probes.TryGetValue(adapter.Name, out var cached) && DateTime.UtcNow - cached.At < StatusCacheTtl) return cached;

        Probe probe;
        if (account is null)
        {
            probe = new Probe(true, null, new AgentAuthState(true, null, "Este adapter não informa o estado da conta."), DateTime.UtcNow);
        }
        else
        {
            var version = await QuickAsync(adapter.Executable, account.VersionArguments, ct);
            if (!version.Started)
            {
                probe = new Probe(false, null, new AgentAuthState(false, null, $"CLI '{adapter.Executable}' não encontrado."), DateTime.UtcNow);
            }
            else
            {
                var status = await QuickAsync(adapter.Executable, account.StatusArguments, ct);
                probe = new Probe(true, version.Stdout.Trim().Split('\n')[0].Trim(),
                    account.ParseStatus(status.Stdout, status.ExitCode), DateTime.UtcNow);
            }
        }
        _probes[adapter.Name] = probe;
        return probe;
    }

    public void Invalidate(string name)
    {
        _probes.TryRemove(name, out _);
        _tests.TryRemove(name, out _);
    }

    // ------------------------------------------------------------------ teste

    /// <summary>Faz uma chamada real e mínima ao CLI (sem gravar AgentRun) e guarda o resultado para a tela.</summary>
    public async Task<AgentTestResult> TestAsync(string name, CancellationToken ct)
    {
        var adapter = adapters.TryGet(name) ?? throw new AgentNotFoundException(name);
        var account = adapter as ICliAgentAccount;
        var prompt = account?.TestPrompt ?? "Responda apenas com a palavra OK.";

        var probe = await ProbeAsync(adapter, account, force: true, ct);
        if (!probe.Installed || !probe.Auth.Authenticated)
            return Store(name, new AgentTestResult(false, probe.Auth.Detail ?? "Agente não conectado.", 0, 0, DateTime.UtcNow));

        using var scope = scopes.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
        var request = new AgentRequest(AgentRole.Dev, prompt, await ScratchAsync(ct), adapter.Name, MaxTurns: 1,
            Timeout: TestTimeout, Ephemeral: true);

        var clock = Stopwatch.StartNew();
        var run = await runner.RunAsync(request, ct);
        var message = run.Output.Trim();
        if (message.Length > 300) message = message[..300] + "…";
        return Store(name, new AgentTestResult(run.Status == AgentRunStatus.Succeeded,
            message.Length == 0 ? "(sem resposta)" : message, (long)clock.Elapsed.TotalMilliseconds, run.CostUsd, DateTime.UtcNow));
    }

    private AgentTestResult Store(string name, AgentTestResult result)
    {
        _tests[name] = result;
        return result;
    }

    // ------------------------------------------------------------------ login por link

    private sealed class LoginSession
    {
        public required string Id { get; init; }
        public required string AdapterName { get; init; }
        public required IRunningProcess Process { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public TaskCompletionSource<string?> Url { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Completa quando o login chegou a um estado final (sucesso, falha ou cancelamento).</summary>
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> Log { get; } = new();
        public volatile LoginState State = LoginState.WaitingForCode;
        public volatile string? Message;
        public string? UrlValue;
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public volatile bool CodeSent;
    }

    public async Task<LoginSessionInfo> StartLoginAsync(string name, CancellationToken ct)
    {
        var adapter = adapters.TryGet(name) ?? throw new AgentNotFoundException(name);
        if (adapter is not ICliAgentAccount { LoginArguments: { } loginArgs } account)
            throw new AgentNotSupportedException($"O agente '{name}' não tem login pela aplicação.");

        foreach (var old in _logins.Values.Where(s => s.AdapterName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
            Finish(old, LoginState.Cancelled, "Substituída por um novo login.");
        foreach (var stale in _logins.Values.Where(s => s.State != LoginState.WaitingForCode && DateTime.UtcNow - s.StartedAt > TimeSpan.FromMinutes(30)).ToList())
            _logins.TryRemove(stale.Id, out _);

        IRunningProcess process;
        try
        {
            // stdin fica aberto: é por ele que o código colado pela pessoa chega ao CLI
            process = await launcher.StartAsync(new LaunchSpec(adapter.Executable, loginArgs, await ScratchAsync(ct), CloseStdin: false), ct);
        }
        catch (ProcessStartException ex)
        {
            return new LoginSessionInfo("", null, LoginState.Failed, $"Não foi possível iniciar o login: {ex.Message}");
        }

        var session = new LoginSession
        {
            Id = Guid.NewGuid().ToString("N"),
            AdapterName = adapter.Name,
            Process = process,
            Cts = new CancellationTokenSource(LoginIdleTimeout),
        };
        _logins[session.Id] = session;
        session.Cts.Token.Register(() =>
        {
            if (session.State == LoginState.WaitingForCode) Finish(session, LoginState.Failed, "Tempo esgotado: inicie o login de novo.");
        });

        _ = PumpAsync(session, process.ReadStdoutLineAsync, account);
        _ = PumpAsync(session, process.ReadStderrLineAsync, account);
        _ = WatchExitAsync(session, adapter, account);

        var url = await Task.WhenAny(session.Url.Task, session.Done.Task, Task.Delay(LoginLinkTimeout, ct)) == session.Url.Task
            ? await session.Url.Task
            : null;
        if (url is null && session.State == LoginState.WaitingForCode)
            Finish(session, LoginState.Failed, "O CLI não devolveu o link de login. " + Tail(session));
        return Snapshot(session);
    }

    public async Task<LoginSessionInfo> SubmitCodeAsync(string name, string sessionId, string code, CancellationToken ct)
    {
        var session = FindSession(name, sessionId);
        code = (code ?? "").Trim();
        if (code.Length == 0 || code.Length > MaxCodeLength || code.Any(char.IsControl))
            return Snapshot(session) with { Message = "Código inválido: cole exatamente o que a página de login mostrou." };
        if (session.State != LoginState.WaitingForCode) return Snapshot(session);
        if (session.CodeSent) return Snapshot(session) with { Message = "O código já foi enviado; aguarde o resultado." };

        session.CodeSent = true;
        await session.Process.WriteStdinAsync(code + "\n", ct);   // se o processo já terminou, o resultado sai do watcher

        await Task.WhenAny(session.Done.Task, Task.Delay(LoginCodeTimeout, ct));
        if (session.State == LoginState.WaitingForCode)
            Finish(session, LoginState.Failed, "O login não terminou a tempo. " + Tail(session));
        return Snapshot(session);
    }

    public LoginSessionInfo GetLogin(string name, string sessionId) => Snapshot(FindSession(name, sessionId));

    public LoginSessionInfo CancelLogin(string name, string sessionId)
    {
        var session = FindSession(name, sessionId);
        if (session.State == LoginState.WaitingForCode) Finish(session, LoginState.Cancelled, "Login cancelado.");
        return Snapshot(session);
    }

    public async Task<AgentInfo> LogoutAsync(string name, CancellationToken ct)
    {
        var adapter = adapters.TryGet(name) ?? throw new AgentNotFoundException(name);
        if (adapter is not ICliAgentAccount { LogoutArguments: { } args })
            throw new AgentNotSupportedException($"O agente '{name}' não tem logout pela aplicação.");
        await QuickAsync(adapter.Executable, args, ct);
        Invalidate(name);
        return await GetAsync(name, force: true, ct);
    }

    private LoginSession FindSession(string name, string sessionId) =>
        _logins.TryGetValue(sessionId, out var s) && s.AdapterName.Equals(name, StringComparison.OrdinalIgnoreCase)
            ? s
            : throw new AgentNotFoundException(name);

    private async Task PumpAsync(LoginSession session, Func<CancellationToken, Task<string?>> readLine, ICliAgentAccount account)
    {
        try
        {
            string? line;
            while ((line = await readLine(CancellationToken.None)) is not null)
            {
                session.Log.Enqueue(line);
                while (session.Log.Count > 30) session.Log.TryDequeue(out _);
                if (session.UrlValue is null && account.ExtractLoginUrl(line) is { } url)
                {
                    session.UrlValue = url;
                    session.Url.TrySetResult(url);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // processo encerrado
        }
    }

    private async Task WatchExitAsync(LoginSession session, ICliAgentAdapter adapter, ICliAgentAccount account)
    {
        try
        {
            var exitCode = await session.Process.WaitForExitAsync(CancellationToken.None);
            session.Exited.TrySetResult();
            session.Url.TrySetResult(null);
            if (session.State != LoginState.WaitingForCode) return;

            // sucesso = o CLI diz que agora está conectado (não confiamos só no código de saída)
            Invalidate(adapter.Name);
            var probe = await ProbeAsync(adapter, account, force: true, CancellationToken.None);
            if (exitCode == 0 && probe.Auth.Authenticated)
                Finish(session, LoginState.Succeeded, probe.Auth.Detail is { Length: > 0 } d ? $"Conectado: {d}" : "Conectado.");
            else
                Finish(session, LoginState.Failed, "Login não concluído. " + Tail(session));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao acompanhar o login do agente {Agent}", adapter.Name);
            Finish(session, LoginState.Failed, "Erro ao acompanhar o login.");
        }
    }

    private static void Finish(LoginSession session, LoginState state, string message)
    {
        if (session.State != LoginState.WaitingForCode) return;
        session.Message = message;
        session.State = state;
        if (state != LoginState.Succeeded) session.Process.Kill();
        session.Url.TrySetResult(null);
        session.Done.TrySetResult();
    }

    private static string Tail(LoginSession s) =>
        string.Join(" ", s.Log.Where(l => !l.Contains("https://", StringComparison.Ordinal)).TakeLast(3)).Trim();

    private static LoginSessionInfo Snapshot(LoginSession s) =>
        new(s.Id, s.State == LoginState.WaitingForCode ? s.UrlValue : s.UrlValue, s.State, s.Message);
}
