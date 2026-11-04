namespace AgentForge.Execution;

/// <summary>Contrato HTTP entre a API (no Docker) e o host agent (no computador). Autenticação: Authorization: Bearer &lt;token&gt;.</summary>
public static class HostAgentRoutes
{
    public const string Info = "/v1/info";
    public const string Processes = "/v1/processes";
    public static string Events(string id) => $"/v1/processes/{id}/events";
    public static string Stdin(string id) => $"/v1/processes/{id}/stdin";
    public static string Process(string id) => $"/v1/processes/{id}";
    public const string FsExists = "/v1/fs/exists";
    public const string FsMkdir = "/v1/fs/mkdir";
    public const string FsDelete = "/v1/fs/delete";
    /// <summary>Chave SSH da máquina: GET = estado, DELETE = apaga; <see cref="SshGenerate"/> cria (a privada nunca sai da máquina).</summary>
    public const string Ssh = "/v1/ssh";
    public const string SshGenerate = "/v1/ssh/generate";
}

public record GenerateKeyRequest(bool Overwrite);

public record HostInfo(string Os, string Version, string ScratchDirectory, IReadOnlyList<string> Roots, IReadOnlyList<string> Executables);

public record StartProcessRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, string? Stdin, bool CloseStdin);
public record StartProcessResponse(string Id);
public record StdinRequest(string? Text, bool Close);
public record PathRequest(string Path);
public record ExistsResponse(bool Exists);
public record HostError(string Error);

/// <summary>Uma linha do fluxo NDJSON de um processo: T = "out" | "err" | "exit".</summary>
public record ProcessEvent(string T, string? D = null, int? C = null);
