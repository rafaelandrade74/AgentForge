using AgentForge.Domain;
using AgentForge.Orchestrator.Agents;

namespace AgentForge.Tests;

public class ClaudeCodeAdapterTests
{
    private readonly ClaudeCodeAdapter _adapter = new();

    [Fact]
    public void Builds_headless_arguments()
    {
        var args = _adapter.BuildArguments(new AgentRequest(AgentRole.Dev, "x", ".", SystemPrompt: "seja breve",
            AllowedTools: "Read,Edit", Model: "sonnet", MaxTurns: 7));

        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", "--append-system-prompt", "seja breve",
             "--allowedTools", "Read,Edit", "--max-turns", "7", "--model", "sonnet"], args);
    }

    [Fact]
    public void Includes_reasoning_effort_when_set()
    {
        var args = _adapter.BuildArguments(new AgentRequest(AgentRole.Dev, "x", ".", ReasoningEffort: "high", MaxTurns: 5));
        var list = args.ToList();
        Assert.Contains("--reasoning-effort", list);
        Assert.Equal("high", list[list.IndexOf("--reasoning-effort") + 1]);
    }

    [Fact]
    public void Omits_optional_arguments_when_empty()
    {
        var args = _adapter.BuildArguments(new AgentRequest(AgentRole.Dev, "x", ".", MaxTurns: 0));
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose"], args);
        Assert.DoesNotContain("--reasoning-effort", args);
    }

    [Fact]
    public void Parses_stream_events()
    {
        Assert.Equal("system", _adapter.ParseEvent("""{"type":"system","subtype":"init","model":"m"}""")!.Kind);
        Assert.Equal(new AgentEventData("assistant", "oi"),
            _adapter.ParseEvent("""{"type":"assistant","message":{"content":[{"type":"text","text":"oi"}]}}"""));
        Assert.Equal("tool",
            _adapter.ParseEvent("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"a"}}]}}""")!.Kind);
        Assert.Equal("tool_result",
            _adapter.ParseEvent("""{"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}""")!.Kind);
        Assert.Equal("raw", _adapter.ParseEvent("ruído qualquer")!.Kind);
        Assert.Null(_adapter.ParseEvent("""{"type":"rate_limit_event"}"""));
        Assert.Null(_adapter.ParseEvent(""));
    }

    [Fact]
    public void Parses_result_with_json_block_and_cost()
    {
        string[] lines =
        [
            """{"type":"system","subtype":"init"}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"pronto\n```json\n{\"ok\":true}\n```","total_cost_usd":0.25}""",
        ];

        var result = _adapter.ParseResult(lines, 0);

        Assert.False(result.IsError);
        Assert.Equal("""{"ok":true}""", result.Json);
        Assert.Equal(0.25m, result.CostUsd);
    }

    [Fact]
    public void Result_flags_error_by_exit_code_or_flag_or_absence()
    {
        Assert.True(_adapter.ParseResult(["""{"type":"result","is_error":true,"result":"x"}"""], 0).IsError);
        Assert.True(_adapter.ParseResult(["""{"type":"result","is_error":false,"result":"x"}"""], 1).IsError);
        Assert.True(_adapter.ParseResult(["lixo"], 0).IsError);
    }

    [Theory]
    [InlineData("texto\n```json\n{\"a\":1}\n```\nfim", "{\"a\":1}")]
    [InlineData("```json\n{\"a\":1}\n```\ndepois\n```json\n{\"b\":2}\n```", "{\"b\":2}")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("```json\n{quebrado\n```", null)]
    [InlineData("sem json", null)]
    public void Extracts_last_valid_json_block(string text, string? expected) =>
        Assert.Equal(expected, JsonBlock.ExtractLast(text));
}
