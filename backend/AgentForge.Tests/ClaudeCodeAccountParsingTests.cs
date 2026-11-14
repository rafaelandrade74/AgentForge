using AgentForge.Orchestrator.Agents;

namespace AgentForge.Tests;

public class ClaudeCodeAccountParsingTests
{
    private readonly ClaudeCodeAdapter _adapter = new();

    [Fact]
    public void Parses_real_status_output_when_not_logged_in()
    {
        var state = _adapter.ParseStatus("""
            {
              "loggedIn": false,
              "authMethod": "none",
              "apiProvider": "firstParty",
              "configDirectory": "/home/app/.claude"
            }
            """, 1);

        Assert.False(state.Authenticated);
        Assert.Equal("none", state.Method);
    }

    [Fact]
    public void Parses_logged_in_status_with_account_details()
    {
        var state = _adapter.ParseStatus("""{"loggedIn":true,"authMethod":"claude.ai","email":"a@b.com","subscriptionType":"max"}""", 0);

        Assert.True(state.Authenticated);
        Assert.Equal("claude.ai", state.Method);
        Assert.Equal("a@b.com · max", state.Detail);
    }

    [Fact]
    public void Garbage_status_is_not_authenticated() =>
        Assert.False(_adapter.ParseStatus("erro qualquer", 1).Authenticated);

    [Theory]
    [InlineData("If the browser didn't open, visit: https://claude.com/cai/oauth/authorize?code=true&state=abc", "https://claude.com/cai/oauth/authorize?code=true&state=abc")]
    [InlineData("Opening browser to sign in…", null)]
    [InlineData("Paste code here if prompted > ", null)]
    public void Extracts_the_login_link(string line, string? expected) =>
        Assert.Equal(expected, _adapter.ExtractLoginUrl(line));

    [Fact]
    public void Account_commands_use_claude_auth()
    {
        Assert.Equal(["auth", "login"], _adapter.LoginArguments);
        Assert.Equal(["auth", "logout"], _adapter.LogoutArguments);
        Assert.Equal("auth", _adapter.StatusArguments[0]);
    }
}
