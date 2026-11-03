using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;

namespace AgentForge.Execution;

/// <summary>
/// Executa no computador hospedeiro por meio do host agent. O processo roda lá (com o login, o PATH e as pastas do
/// usuário), e a saída chega em NDJSON, linha a linha, sem fila intermediária.
/// </summary>
public sealed class RemoteProcessLauncher : IProcessLauncher
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private HostInfo? _info;

    public RemoteProcessLauncher(HttpClient http, string baseUrl, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("O token do host agent é obrigatório.", nameof(token));
        _http = http;
        _baseUrl = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.BaseAddress = _baseUrl;
        _http.Timeout = Timeout.InfiniteTimeSpan;   // os fluxos de saída são longos; cada chamada usa o seu CancellationToken
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public string Mode => "host";
    public string Description => $"no computador, pelo host agent ({_baseUrl.Authority}{(_info is null ? "" : ", " + _info.Os)})";

    public async Task<HostInfo> InfoAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(HostAgentRoutes.Info.TrimStart('/'), ct);
            await EnsureOkAsync(response, ct);
            return _info = (await response.Content.ReadFromJsonAsync<HostInfo>(Json, ct))!;
        }
        catch (HttpRequestException ex)
        {
            throw new ProcessStartException($"Host agent inacessível em {_baseUrl}: {ex.Message}");
        }
    }

    public async Task<string> ScratchDirectoryAsync(CancellationToken ct) => (_info ?? await InfoAsync(ct)).ScratchDirectory;

    public async Task<IRunningProcess> StartAsync(LaunchSpec spec, CancellationToken ct)
    {
        string id;
        try
        {
            using var response = await _http.PostAsJsonAsync(HostAgentRoutes.Processes.TrimStart('/'),
                new StartProcessRequest(spec.Executable, spec.Arguments, spec.WorkingDirectory, spec.Stdin, spec.CloseStdin), Json, ct);
            await EnsureOkAsync(response, ct);
            id = (await response.Content.ReadFromJsonAsync<StartProcessResponse>(Json, ct))!.Id;
        }
        catch (HttpRequestException ex)
        {
            throw new ProcessStartException($"Host agent inacessível em {_baseUrl}: {ex.Message}");
        }
        return await RemoteRunningProcess.AttachAsync(_http, id, ct);
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct) => FsAsync(HostAgentRoutes.FsExists, path, ct, reads: true);
    public Task CreateDirectoryAsync(string path, CancellationToken ct) => FsAsync(HostAgentRoutes.FsMkdir, path, ct, reads: false);
    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => FsAsync(HostAgentRoutes.FsDelete, path, ct, reads: false);

    private async Task<bool> FsAsync(string route, string path, CancellationToken ct, bool reads)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(route.TrimStart('/'), new PathRequest(path), Json, ct);
            await EnsureOkAsync(response, ct);
            return reads && (await response.Content.ReadFromJsonAsync<ExistsResponse>(Json, ct))!.Exists;
        }
        catch (HttpRequestException ex)
        {
            throw new ProcessStartException($"Host agent inacessível em {_baseUrl}: {ex.Message}");
        }
    }

    internal static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = "";
        try
        {
            detail = (await response.Content.ReadFromJsonAsync<HostError>(Json, ct))?.Error ?? "";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // corpo não é JSON
        }
        throw new ProcessStartException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "O host agent recusou o token (Agents:HostAgent:Token).",
            _ => $"Host agent respondeu {(int)response.StatusCode}: {detail}".TrimEnd(' ', ':'),
        });
    }

    private sealed class RemoteRunningProcess : IRunningProcess
    {
        private readonly HttpClient _http;
        private readonly string _id;
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<string> _out = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _err = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HttpResponseMessage? _stream;

        private RemoteRunningProcess(HttpClient http, string id)
        {
            _http = http;
            _id = id;
        }

        public static async Task<IRunningProcess> AttachAsync(HttpClient http, string id, CancellationToken ct)
        {
            var process = new RemoteRunningProcess(http, id);
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, HostAgentRoutes.Events(id).TrimStart('/'));
                process._stream = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                await EnsureOkAsync(process._stream, ct);
            }
            catch
            {
                process.Kill();
                throw;
            }
            _ = process.PumpAsync();
            return process;
        }

        private async Task PumpAsync()
        {
            try
            {
                await using var body = await _stream!.Content.ReadAsStreamAsync(_cts.Token);
                using var reader = new StreamReader(body);
                string? line;
                while ((line = await reader.ReadLineAsync(_cts.Token)) is not null)
                {
                    if (line.Length == 0) continue;
                    var evt = JsonSerializer.Deserialize<ProcessEvent>(line, Json);
                    switch (evt?.T)
                    {
                        case "out": _out.Writer.TryWrite(evt.D ?? ""); break;
                        case "err": _err.Writer.TryWrite(evt.D ?? ""); break;
                        case "exit": _exit.TrySetResult(evt.C ?? -1); break;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
            {
                // conexão perdida: tratado abaixo como término anormal
            }
            finally
            {
                _exit.TrySetResult(-1);
                _out.Writer.TryComplete();
                _err.Writer.TryComplete();
            }
        }

        public async Task WriteStdinAsync(string text, CancellationToken ct = default) => await SendStdinAsync(text, close: false, ct);
        public async Task CloseStdinAsync() => await SendStdinAsync(null, close: true, CancellationToken.None);

        private async Task SendStdinAsync(string? text, bool close, CancellationToken ct)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync(HostAgentRoutes.Stdin(_id).TrimStart('/'), new StdinRequest(text, close), Json, ct);
                // 404/409: o processo já terminou; o resultado vem pelo fluxo de eventos
            }
            catch (HttpRequestException)
            {
                // host agent fora do ar: o fluxo de eventos acusa
            }
        }

        public Task<string?> ReadStdoutLineAsync(CancellationToken ct) => ReadAsync(_out, ct);
        public Task<string?> ReadStderrLineAsync(CancellationToken ct) => ReadAsync(_err, ct);

        private static async Task<string?> ReadAsync(Channel<string> channel, CancellationToken ct) =>
            await channel.Reader.WaitToReadAsync(ct) && channel.Reader.TryRead(out var line) ? line : null;

        public Task<int> WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

        public void Kill()
        {
            // fire-and-forget: pede para o host agent matar a árvore de processos
            _ = Task.Run(async () =>
            {
                try
                {
                    using var response = await _http.DeleteAsync(HostAgentRoutes.Process(_id).TrimStart('/'));
                }
                catch (HttpRequestException)
                {
                    // sem resposta: a desconexão do fluxo também faz o host agent matar o processo
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _stream?.Dispose();
            _cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
