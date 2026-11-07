using System.Globalization;
using AgentForge.Orchestrator.Agents;

namespace AgentForge.Tests;

public class UsageLimitTests
{
    private readonly ClaudeCodeAdapter _adapter = new();

    private static string ResultError(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new { type = "result", subtype = "error", is_error = true, result = text, total_cost_usd = 0 });

    private static long Epoch(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string Rejected(DateTime reset, string? type) =>
        "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"rejected\",\"resetsAt\":" + Epoch(reset) +
        (type is null ? "" : ",\"rateLimitType\":\"" + type + "\"") + "}}";

    [Fact]
    public void A_rejected_rate_limit_event_gives_the_reset_time_from_resetsAt()
    {
        var reset = DateTime.UtcNow.AddHours(3);
        string[] lines =
        [
            Rejected(reset, "five_hour"),
            ResultError("You've hit your limit"),
        ];

        var result = _adapter.ParseResult(lines, exitCode: 1);

        Assert.True(result.IsError);
        var limit = Assert.IsType<UsageLimit>(result.Limit);
        Assert.Equal(Epoch(reset), Epoch(limit.ResetsAt!.Value));
        Assert.Equal(limit.ResetsAt + UsageLimitRules.Margin, limit.RetryAt);
        Assert.Equal(UsageLimitKinds.Session, limit.Kind);
    }

    [Fact]
    public void A_weekly_rejection_is_recognised_as_weekly()
    {
        var reset = DateTime.UtcNow.AddDays(3);
        string[] lines =
        [
            Rejected(reset, "seven_day"),
            ResultError("You've hit your weekly limit"),
        ];

        Assert.Equal(UsageLimitKinds.Weekly, _adapter.ParseResult(lines, 1).Limit!.Kind);
    }

    [Fact]
    public void An_allowed_rate_limit_event_is_not_a_limit_and_stays_silent()
    {
        const string allowed = """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1,"rateLimitType":"five_hour"}}""";
        const string rejected = """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1,"rateLimitType":"five_hour"}}""";

        Assert.Null(_adapter.ParseEvent(allowed));
        Assert.Equal("rate_limit", _adapter.ParseEvent(rejected)!.Kind);
        Assert.Null(_adapter.ParseResult([allowed, """{"type":"result","is_error":false,"result":"ok"}"""], 0).Limit);
    }

    [Fact]
    public void The_legacy_pipe_epoch_message_is_understood()
    {
        var reset = DateTime.UtcNow.AddHours(2);

        var limit = _adapter.ParseResult([ResultError($"Claude AI usage limit reached|{Epoch(reset)}")], 1).Limit;

        Assert.NotNull(limit);
        Assert.Equal(Epoch(reset), Epoch(limit!.ResetsAt!.Value));
    }

    [Fact]
    public void A_clock_time_with_a_timezone_in_the_message_becomes_the_next_occurrence_in_utc()
    {
        var limit = _adapter.ParseResult([ResultError("You've hit your limit · resets 3pm (UTC)")], 1).Limit;

        Assert.NotNull(limit);
        Assert.NotNull(limit!.ResetsAt);
        Assert.Equal(15, limit.ResetsAt!.Value.Hour);
        Assert.InRange(limit.ResetsAt.Value, DateTime.UtcNow, DateTime.UtcNow.AddHours(24).AddMinutes(1));
    }

    [Fact]
    public void A_dated_reset_in_the_message_is_read_with_month_and_day()
    {
        var day = DateTime.UtcNow.AddDays(2);
        var text = $"You've hit your weekly limit · resets {day.ToString("MMM d", CultureInfo.InvariantCulture)}, 9:30am (UTC)";

        var limit = _adapter.ParseResult([ResultError(text)], 1).Limit;

        Assert.Equal(new DateTime(day.Year, day.Month, day.Day, 9, 30, 0, DateTimeKind.Utc), limit!.ResetsAt);
        Assert.Equal(UsageLimitKinds.Weekly, limit.Kind);
    }

    [Fact]
    public void Without_a_readable_reset_the_limit_probes_periodically()
    {
        var before = DateTime.UtcNow;

        var limit = _adapter.ParseResult([ResultError("You've hit your limit")], 1).Limit;

        Assert.NotNull(limit);
        Assert.Null(limit!.ResetsAt);
        Assert.InRange(limit.RetryAt, before + UsageLimitRules.ProbeInterval, DateTime.UtcNow + UsageLimitRules.ProbeInterval);
    }

    [Fact]
    public void A_reset_in_the_past_or_far_away_is_not_trusted()
    {
        var past = _adapter.ParseResult([ResultError($"Claude AI usage limit reached|{Epoch(DateTime.UtcNow.AddHours(-2))}")], 1).Limit;
        var far = _adapter.ParseResult([ResultError($"Claude AI usage limit reached|{Epoch(DateTime.UtcNow.AddDays(40))}")], 1).Limit;

        Assert.Null(past!.ResetsAt);
        Assert.Null(far!.ResetsAt);
        Assert.True(past.RetryAt > DateTime.UtcNow);
    }

    [Theory]
    [InlineData("O build falhou: testes vermelhos")]
    [InlineData("Error: max turns reached")]
    [InlineData("")]
    public void Ordinary_failures_are_not_usage_limits(string text)
    {
        var result = _adapter.ParseResult([ResultError(text)], 1);

        Assert.True(result.IsError);
        Assert.Null(result.Limit);
    }

    [Fact]
    public void A_successful_run_that_mentions_limits_is_never_a_usage_limit()
    {
        var result = _adapter.ParseResult(["""{"type":"result","is_error":false,"result":"adicionei rate limit na API"}"""], 0);

        Assert.False(result.IsError);
        Assert.Null(result.Limit);
    }

    [Fact]
    public void The_cli_dying_after_a_rejected_event_still_reports_the_limit()
    {
        var reset = DateTime.UtcNow.AddHours(1);
        var line = Rejected(reset, null);

        var result = _adapter.ParseResult([line], 1);

        Assert.True(result.IsError);
        Assert.NotNull(result.Limit);
    }

    [Fact]
    public void The_tracker_holds_a_limit_until_its_retry_time_per_environment_and_clears_on_success()
    {
        var tracker = new UsageLimitTracker();
        var soon = UsageLimitRules.Create(DateTime.UtcNow.AddHours(1), UsageLimitKinds.Session, "x", DateTime.UtcNow);
        var expired = new UsageLimit(null, DateTime.UtcNow.AddSeconds(-1), UsageLimitKinds.Rate, "y");

        tracker.Record("a", "claude-code", soon);
        tracker.Record("b", "claude-code", expired);

        Assert.Same(soon, tracker.Active("a", "claude-code"));
        Assert.Null(tracker.Active("b", "claude-code"));       // já na hora de sondar
        Assert.Null(tracker.Active("c", "claude-code"));       // outra conta não é afetada
        Assert.Single(tracker.Snapshot());

        tracker.Clear("a", "claude-code");
        Assert.Null(tracker.Active("a", "claude-code"));
        Assert.Empty(tracker.Snapshot());
    }
}
