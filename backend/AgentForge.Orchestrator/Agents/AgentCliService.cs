using System.Collections.Concurrent;
using System.Diagnostics;
using AgentForge.Domain;
using AgentForge.Execution;
using AgentForge.Infrastructure.Persistence;
using AgentForge.Orchestrator.Environments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentForge.Orchestrator.Agents;

public class AgentNotFoundException(string name) : Exception($"Agente '{name}' não existe.");
public class AgentNotSupportedException(string message) : Exception(message);

/// <summary>
/// Tela "Agentes": diz se cada CLI está instalado e conectado, testa a conexão de verdade e conduz o login por link
/// (o CLI imprime o link e espera o código; a pessoa autoriza no navegador e cola o código aqui). Tudo vale POR AMBIENTE:
/// cada máquina tem o seu login do CLI, então status, teste e login são feitos na máquina escolhida (null = padrão do servidor).
/// </summary>
public sealed class AgentCliService(
    AgentAdapterRegistry adapters, IServiceScopeFactory scopes, IEnvironmentLaunchers environments, ILogger<AgentCliService> logger)
{
    /// <summary>Um ambiente só, com o launcher dado (testes e instalações sem ambientes cadastrados).</summary>
    public AgentCliService(AgentAdapterRegistry adapters, IServiceScopeFactory scopes, IProcessLauncher launcher, ILogger<AgentCliService> logger)
        : this(adapters, scopes, new SingleEnvironment(launcher), logger) { }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LoginLinkTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LoginCodeTimeout = TimeSpan.FromSeconds(40);
    public static TimeSpan LoginIdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
    private const int MaxCodeLength = 600;

    private sealed record Probe(bool Installed, string? Version, AgentAuthState Auth, DateTime At);

    // chaves "<ambiente>|<agente>": o estado de uma máquina nunca vaza para outra
    private readonly ConcurrentDictionary<string, Probe> _probes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentTestResult> _tests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LoginSession> _logins = new();

    private static string EnvKey(Guid? environmentId) => environmentId?.ToString("N") ?? "default";
    private static string CacheKey(string environmentKey, string adapterName) => $"{environmentKey}|{adapterName}";

    // ------------------------------------------------------------------ ambiente

    /// <summary>Onde os CLIs rodam no ambiente: no container/computador da API ("local") ou numa máquina, pelo host agent ("host").</summary>
    public async Task<(string Mode, string Description)> EnvironmentAsync(Guid? environmentId, CancellationToken ct)
    {
        var (target, _) = await TargetAsync(environmentId, ct);
        return (target.Launcher.Mode, target.Launcher.Description);
    }

    /// <exception cref="EnvironmentUnavailableException">ambiente inexistente ou com token ilegível.</exception>
    private async Task<(EnvironmentTarget Target, string Key)> TargetAsync(Guid? environmentId, CancellationToken ct) =>
        (await environments.ResolveAsync(environmentId, ct), EnvKey(environmentId));

    private static async Task<CompletedProcess> QuickAsync(IProcessLauncher launcher, string executable, IReadOnlyList<string> args, CancellationToken ct) =>
        await launcher.RunAsync(new LaunchSpec(executable, args, await ScratchAsync(launcher, ct)), ProbeTimeout, ct);

    private static async Task<string> ScratchAsync(IProcessLauncher launcher, CancellationToken ct)
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

    // ------------------------------------------------------------------ status

    public Task<IReadOnlyList<AgentInfo>> ListAsync(CancellationToken ct) => ListAsync(null, ct);

    public async Task<IReadOnlyList<AgentInfo>> ListAsync(Guid? environmentId, CancellationToken ct)
    {
        var (target, key) = await TargetAsync(environmentId, ct);

        Dictionary<string, List<string>> usage;
        using (var scope = scopes.CreateScope())
        {
            var configs = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .AgentRoleConfigs.AsNoTracking().Select(c => new { c.Role, c.AdapterName }).ToListAsync(ct);
            usage = configs.GroupBy(c => c.AdapterName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Role.ToString()).ToList(), StringComparer.OrdinalIgnoreCase);
        }

        var list = adapters.All.ToList();
        var infos = await Task.WhenAll(list.Select(a => InfoAsync(target, key, a, usage.GetValueOrDefault(a.Name) ?? [], force: false, ct)));
        return infos;
    }

    public Task<AgentInfo> GetAsync(string name, bool force, CancellationToken ct) => GetAsync(null, name, force, ct);

    public async Task<AgentInfo> GetAsync(Guid? environmentId, string name, bool force, CancellationToken ct)
    {
        var adapter = adapters.TryGet(name) ?? throw new AgentNotFoundException(name);
        var (target, key) = await TargetAsync(environmentId, ct);
        return await InfoAsync(target, key, adapter, [], force, ct);
    }

    private async Task<AgentInfo> InfoAsync(EnvironmentTarget target, string key, ICliAgentAdapter adapter,
        IReadOnlyList<string> roles, bool force, CancellationToken ct)
    {
        var account = adapter as ICliAgentAccount;
        var probe = await ProbeAsync(target, key, adapter, account, force, ct);
        _tests.TryGetValue(CacheKey(key, adapter.Name), out var lastTest);
        return new AgentInfo(adapter.Name, account?.DisplayName ?? adapter.Name, probe.Installed, probe.Version,
            probe.Auth.Authenticated, probe.Auth.Method, probe.Auth.Detail, account?.LoginArguments is not null, roles, lastTest);
    }

    private async Task<Probe> ProbeAsync(EnvironmentTarget target, string key, ICliAgentAdapter adapter, ICliAgentAccount? account,
        bool force, CancellationToken ct)
    {
        var cacheKey = CacheKey(key, adapter.Name);
        if (!force && _probes.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.At < StatusCacheTtl) return cached;

        Probe probe;
        if (account is null)
        {
            probe = new Probe(true, null, new AgentAuthState(true, null, "Este adapter não informa o estado da conta."), DateTime.UtcNow);
        }
        else
        {
            var version = await QuickAsync(target.Launcher, adapter.Executable, account.VersionArguments, ct);
            if (!version.Started)
            {
                // host agent fora do ar / token recusado: o motivo de verdade vem do launcher, não "CLI não encontrado"
                var stderr = version.Stderr.Trim();
                var why = stderr.Contains("inacessível", StringComparison.Ordinal) || stderr.Contains("recusou o token", StringComparison.Ordinal)
                    ? stderr
                    : $"CLI '{adapter.Executable}' não encontrado.";
                probe = new Probe(false, null, new AgentAuthState(false, null, why), DateTime.UtcNow);
            }
            else
            {
                var status = await QuickAsync(target.Launcher, adapter.Executable, account.StatusArguments, ct);
                probe = new Probe(true, version.Stdout.Trim().Split('\n')[0].Trim(),
                    account.ParseStatus(status.Stdout, status.ExitCode), DateTime.UtcNow);
            }
        }
        _probes[cacheKey] = probe;
        return probe;
    }

    private void Invalidate(string key, string name)
    {
        _probes.TryRemove(CacheKey(key, name), out _);
        _tests.TryRemove(CacheKey(key, name), out _);
    }

    // ------------------------------------------------------------------ teste

    public Task<AgentTestResult> TestAsync(string name, CancellationToken ct) => TestAsync(null, name, ct);

    /// <summary>Faz uma chamada real e mínima ao CLI do ambiente (sem gravar AgentRun) e guarda o resultado para a tela.</summary>
    public async Task<AgentTestResult> TestAsync(Guid? environmentId, string name, CancellationToken ct)
    {
        var adapter = adapters.TryGet(name) ?? throw new AgentNotFoundException(name);
        var account = adapter as ICliAgentAccount;
        var prompt = account?.TestPrompt ?? "Responda apenas com a palavra OK.";
        var (target, key) = await TargetAsync(environmentId, ct);

        var probe = await ProbeAsync(target, key, adapter, account, force: true, ct);
        if (!probe.Installed || !probe.Auth.Authenticated)
            return Store(key, name, new AgentTestResult(false, probe.Auth.Detail ?? "Agente não conectado.", 0, 0, DateTime.UtcNow));

        using var scope = scopes.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
        var request = new AgentRequest(AgentRole.Dev, prompt, await ScratchAsync(target.Launcher, ct), adapter.Name, MaxTurns: 1,
            Timeout: TestTimeout, Ephemeral: true, EnvironmentId: environmentId);

        var clock = Stopwatch.StartNew();
        var run = await runner.RunAsync(request, ct);
        var message = run.Output.Trim();
        if (message.Length > 300) message = message[..300] + "…";
        return Store(key, name, new AgentTestResult(run.Status == AgentRunStatus.Succeeded,
            message.Length == 0 ? "(sem resposta)" : message, (long)clock.Elapsed.TotalMilliseconds, run.CostUsd, DateTime.UtcNow));
    }

    private AgentTestResult Store(string key, string name, AgentTestResult result)
    {
        _tests[CacheKey(key, name)] = result;
        return result;
    }

    // ------------------------------------------------------------------ login por link

    private sealed class LoginSession
    {
        public required string Id { get; init; }
        public required string AdapterName { get; init; }
        /// <summary>Ambiente onde o login roda; outro ambiente não enxerga nem conduz esta sessão.</summary>
        public required string EnvironmentKey { get; init; }
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

    public Task<LoginSessionInfo> StartLoginAsync(string name, CancellationToken ct) => StartLoginAsync(null, name, ct);

    public async Task<LoginSessionInfo> StartLoginAsync(Guid? environmentId, string name, CancellationToken ct)
    {
        var adapter = adapters.TryGet(name) ?? throw new AgentNotFoundException(name);
        if (adapter is not ICliAgentAccount { LoginArguments: { } loginArgs } account)
            throw new AgentNotSupportedException($"O agente '{name}' não tem login pela aplicação.");
        var (target, key) = await TargetAsync(environmentId, ct);

        foreach (var old in _logins.Values
                     .Where(s => s.EnvironmentKey == key && s.AdapterName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())
            Finish(old, LoginState.Cancelled, "Substituída por um novo login.");
        foreach (var stale in _logins.Values.Where(s => s.State != LoginState.WaitingForCode && DateTime.UtcNow - s.StartedAt > TimeSpan.FromMinutes(30)).ToList())
            _logins.TryRemove(stale.Id, out _);

        IRunningProcess process;
        try
        {
            // stdin fica aberto: é por ele que o código colado pela pessoa chega ao CLI
            process = await target.Launcher.StartAsync(
                new LaunchSpec(adapter.Executable, loginArgs, await ScratchAsync(target.Launcher, ct), CloseStdin: false), ct);
        }
        catch (ProcessStartException ex)
        {
            return new LoginSessionInfo("", null, LoginState.Failed, $"Não foi possível iniciar o login: {ex.Message}");
        }

        var session = new LoginSession
        {
            Id = Guid.NewGuid().ToString("N"),
            AdapterName = adapter.Name,
            EnvironmentKey = key,
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
        _ = WatchExitAsync(session, target, adapter, account);

        var url = await Task.WhenAny(session.Url.Task, session.Done.Task, Task.Delay(LoginLinkTimeout, ct)) == session.Url.Task
            ? await session.Url.Task
            : null;
        if (url is null && session.State == LoginState.WaitingForCode)
            Finish(session, LoginState.Failed, "O CLI não devolveu o link de login. " + Tail(session));
        return Snapshot(session);
    }

    public Task<LoginSessionInfo> SubmitCodeAsync(string name, string sessionId, string code, CancellationToken ct) =>
        SubmitCodeAsync(null, name, sessionId, code, ct);

    public async Task<LoginSessionInfo> SubmitCodeAsync(Guid? environmentId, string name, string sessionId, string code, CancellationToken ct)
    {
        var session = FindSession(environmentId, name, sessionId);
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

    public LoginSessionInfo GetLogin(string name, string sessionId) => GetLogin(null, name, sessionId);

    public LoginSessionInfo GetLogin(Guid? environmentId, string name, string sessionId) =>
        Snapshot(FindSession(environmentId, name, sessionId));

    public LoginSessionInfo CancelLogin(string name, string sessionId) => CancelLogin(null, name, sessionId);

    public LoginSessionInfo CancelLogin(Guid? environmentId, string name, string sessionId)
    {
        var session = FindSession(environmentId, name, sessionId);
        if (session.State == LoginState.WaitingForCode) Finish(session, LoginState.Cancelled, "Login cancelado.");
        return Snapshot(session);
    }

    public Task<AgentInfo> LogoutAsync(string name, CancellationToken ct) => LogoutAsync(null, name, ct);

    public async Task<AgentInfo> LogoutAsync(Guid? environmentId, string name, CancellationToken ct)
    {
        var adapter = adapters.TryGet(name) ?? throw new AgentNotFoundException(name);
        if (adapter is not ICliAgentAccount { LogoutArguments: { } args })
            throw new AgentNotSupportedException($"O agente '{name}' não tem logout pela aplicação.");
        var (target, key) = await TargetAsync(environmentId, ct);
        await QuickAsync(target.Launcher, adapter.Executable, args, ct);
        Invalidate(key, name);
        return await GetAsync(environmentId, name, force: true, ct);
    }

    /// <summary>A sessão de login só é encontrada pelo mesmo agente E pelo mesmo ambiente em que foi iniciada.</summary>
    private LoginSession FindSession(Guid? environmentId, string name, string sessionId) =>
        _logins.TryGetValue(sessionId, out var s)
        && s.AdapterName.Equals(name, StringComparison.OrdinalIgnoreCase)
        && s.EnvironmentKey == EnvKey(environmentId)
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

    private async Task WatchExitAsync(LoginSession session, EnvironmentTarget target, ICliAgentAdapter adapter, ICliAgentAccount account)
    {
        try
        {
            var exitCode = await session.Process.WaitForExitAsync(CancellationToken.None);
            session.Exited.TrySetResult();
            session.Url.TrySetResult(null);
            if (session.State != LoginState.WaitingForCode) return;

            // sucesso = o CLI diz que agora está conectado (não confiamos só no código de saída)
            Invalidate(session.EnvironmentKey, adapter.Name);
            var probe = await ProbeAsync(target, session.EnvironmentKey, adapter, account, force: true, CancellationToken.None);
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
