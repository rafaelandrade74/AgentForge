using AgentForge.Orchestrator.Environments;
using Microsoft.AspNetCore.DataProtection;

namespace AgentForge.Api.Environments;

/// <summary>
/// Protege os tokens dos host agents com o Data Protection do ASP.NET. As chaves ficam numa pasta própria
/// (DataProtection:KeysDir, um volume no Docker): um dump do banco, sozinho, não revela os tokens.
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("AgentForge.ExecutionEnvironment.Token.v1");

    public string Protect(string plain) => _protector.Protect(plain);
    public string Unprotect(string protectedText) => _protector.Unprotect(protectedText);
}
