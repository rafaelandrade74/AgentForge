using AgentForge.Domain;
using AgentForge.Orchestrator.Agents;

namespace AgentForge.Tests;

public class AdapterRegistryTests
{
    private sealed class MinimalAdapter : ICliAgentAdapter
    {
        public string Name => "fake";
        public string Executable => "echo";
        public IReadOnlyList<string> BuildArguments(AgentRequest request) => [];
        public AgentEventData? ParseEvent(string line) => null;
        public AgentResult ParseResult(IReadOnlyList<string> stdoutLines, int exitCode) => new("", null, 0, false);
    }

    [Fact]
    public void Rejects_unknown_adapter_and_matches_case_insensitively()
    {
        var registry = new AgentAdapterRegistry([new MinimalAdapter()]);
        Assert.Throws<InvalidOperationException>(() => registry.Get("codex"));
        Assert.Equal("fake", registry.Get("FAKE").Name);
    }
}
