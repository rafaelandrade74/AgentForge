using AgentForge.Api.Chat;

namespace AgentForge.Tests;

public class ChatReplyTests
{
    private const string Block = "{\"demands\":[{\"title\":\"Filtro\",\"description\":\"d\"}]}";

    [Fact]
    public void The_trailing_json_block_becomes_proposals_and_leaves_the_text()
    {
        var output = $"Claro, proponho isto.\n```json\n{Block}\n```";

        var (text, proposals) = ChatReply.Split(output, Block);

        Assert.Equal("Claro, proponho isto.", text);
        var p = Assert.Single(proposals);
        Assert.Equal("Filtro", p.Title);
        Assert.Null(p.DemandId);
    }

    [Fact]
    public void A_reply_without_proposals_is_kept_as_is()
    {
        var (text, proposals) = ChatReply.Split("  Só uma resposta.  ", null);

        Assert.Equal("Só uma resposta.", text);
        Assert.Empty(proposals);
    }

    [Fact]
    public void Json_that_is_not_the_expected_shape_gives_no_proposals_and_keeps_the_block()
    {
        const string other = "{\"ok\":true}";
        var output = $"Texto\n```json\n{other}\n```";

        var (text, proposals) = ChatReply.Split(output, other);

        Assert.Empty(proposals);
        Assert.Contains("```json", text);
    }

    [Fact]
    public void Proposals_are_capped_titles_trimmed_and_empty_titles_dropped()
    {
        var many = string.Join(",", Enumerable.Range(1, 9).Select(i => $"{{\"title\":\"T{i}\"}}"));
        var json = $"{{\"demands\":[{{\"title\":\"  \"}},{many},{{\"title\":\"{new string('x', 500)}\"}}]}}";

        var parsed = ChatReply.Parse(json);

        Assert.Equal(ChatReply.MaxProposals, parsed.Count);
        Assert.All(parsed, p => Assert.InRange(p.Title.Length, 1, ChatReply.MaxTitle));
        Assert.Equal("T1", parsed[0].Title);
        Assert.Equal("", parsed[0].Description);
    }

    [Theory]
    [InlineData("não é json")]
    [InlineData("[1,2]")]
    [InlineData("{\"demands\":\"x\"}")]
    [InlineData("")]
    public void Garbage_never_throws(string json) => Assert.Empty(ChatReply.Parse(json));
}
