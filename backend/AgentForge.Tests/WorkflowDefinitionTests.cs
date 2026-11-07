using AgentForge.Domain;
using AgentForge.Orchestrator.Workflow;

namespace AgentForge.Tests;

public class WorkflowDefinitionTests
{
    [Fact]
    public void Happy_path_follows_the_documented_states()
    {
        var path = new (WorkflowEvent Event, WorkItemState Expected)[]
        {
            (WorkflowEvent.HumanApproved, WorkItemState.Todo),
            (WorkflowEvent.Started, WorkItemState.InDevelopment),
            (WorkflowEvent.DevDone, WorkItemState.TechLeadReview),
            (WorkflowEvent.TechLeadApproved, WorkItemState.SecurityReview),
            (WorkflowEvent.SecurityApproved, WorkItemState.Qa),
            (WorkflowEvent.QaPassed, WorkItemState.AwaitingApproval),
            (WorkflowEvent.HumanApproved, WorkItemState.Done),
        };
        var state = WorkItemState.Backlog;
        foreach (var (evt, expected) in path)
        {
            state = WorkflowDefinition.Next(state, evt) ?? throw new Xunit.Sdk.XunitException($"{state} + {evt} deveria ser válido");
            Assert.Equal(expected, state);
        }
    }

    [Theory]
    [InlineData(WorkItemState.TechLeadReview, WorkflowEvent.ChangesRequested)]
    [InlineData(WorkItemState.SecurityReview, WorkflowEvent.ChangesRequested)]
    [InlineData(WorkItemState.Qa, WorkflowEvent.QaFailed)]
    [InlineData(WorkItemState.AwaitingApproval, WorkflowEvent.HumanRejected)]
    [InlineData(WorkItemState.Blocked, WorkflowEvent.Retry)]
    public void Failed_reviews_go_back_to_the_dev(WorkItemState from, WorkflowEvent evt) =>
        Assert.Equal(WorkItemState.InDevelopment, WorkflowDefinition.Next(from, evt));

    [Fact]
    public void Terminal_states_accept_nothing_and_everything_else_can_be_cancelled()
    {
        foreach (var evt in Enum.GetValues<WorkflowEvent>())
        {
            Assert.Null(WorkflowDefinition.Next(WorkItemState.Done, evt));
            Assert.Null(WorkflowDefinition.Next(WorkItemState.Cancelled, evt));
        }
        foreach (var s in Enum.GetValues<WorkItemState>().Where(s => !WorkflowDefinition.IsTerminal(s)))
            Assert.Equal(WorkItemState.Cancelled, WorkflowDefinition.Next(s, WorkflowEvent.Cancel));
    }

    [Fact]
    public void Cannot_skip_reviews()
    {
        Assert.Null(WorkflowDefinition.Next(WorkItemState.InDevelopment, WorkflowEvent.QaPassed));
        Assert.Null(WorkflowDefinition.Next(WorkItemState.InDevelopment, WorkflowEvent.HumanApproved));
        Assert.Null(WorkflowDefinition.Next(WorkItemState.Qa, WorkflowEvent.HumanApproved));
        Assert.Null(WorkflowDefinition.Next(WorkItemState.Backlog, WorkflowEvent.Started));
    }

    [Fact]
    public void Three_returns_is_the_rework_limit() => Assert.Equal(3, WorkflowDefinition.MaxRework);

    [Fact]
    public void Todo_can_be_blocked_by_agent_failure()
    {
        Assert.Equal(WorkItemState.Blocked, WorkflowDefinition.Next(WorkItemState.Todo, WorkflowEvent.AgentFailed));
    }
}

public class OutputParsersTests
{
    [Fact]
    public void Plan_accepts_a_valid_dag_and_string_or_list_criteria()
    {
        var r = OutputParsers.ParsePlan("""{"tasks":[{"key":"a","title":"A","acceptanceCriteria":"x"},{"key":"b","title":"B","acceptanceCriteria":["y","z"],"dependsOn":["a"],"estimate":5}]}""");

        Assert.True(r.Ok, r.Error);
        Assert.Equal("x", r.Value![0].AcceptanceCriteria);
        Assert.Equal("- y\n- z", r.Value[1].AcceptanceCriteria);
        Assert.Equal(5, r.Value[1].Estimate);
    }

    [Theory]
    [InlineData("""{"tasks":[]}""", "não vazia")]
    [InlineData("""{"tasks":[{"key":"a","title":"A","dependsOn":["a"]}]}""", "depende dela mesma")]
    [InlineData("""{"tasks":[{"key":"a","title":"A","dependsOn":["x"]}]}""", "não existe")]
    [InlineData("""{"tasks":[{"key":"a","title":"A","dependsOn":["b"]},{"key":"b","title":"B","dependsOn":["a"]}]}""", "ciclo")]
    [InlineData("""{"tasks":[{"key":"a","title":"A"},{"key":"a","title":"B"}]}""", "únicas")]
    [InlineData("""{"tasks":[{"title":"sem key"}]}""", "key")]
    [InlineData("não é json", "JSON")]
    [InlineData(null, "JSON")]
    public void Plan_rejects_bad_input(string? json, string errorPart) =>
        Assert.Contains(errorPart, OutputParsers.ParsePlan(json).Error);

    [Fact]
    public void Dev_requires_summary_and_commit()
    {
        Assert.True(OutputParsers.ParseDev("""{"summary":"s","commit":"abc1234"}""").Ok);
        Assert.False(OutputParsers.ParseDev("""{"summary":"s"}""").Ok);
        Assert.False(OutputParsers.ParseDev("""{"commit":"abc1234"}""").Ok);
    }

    [Fact]
    public void Review_requires_findings_when_changes_are_requested()
    {
        Assert.True(OutputParsers.ParseReview("""{"verdict":"approved"}""").Value!.Approved);
        Assert.False(OutputParsers.ParseReview("""{"verdict":"changes_requested"}""").Ok);
        var r = OutputParsers.ParseReview("""{"verdict":"changes_requested","findings":[{"file":"a.cs","line":3,"severity":"HIGH","message":"m"},{"message":"sem severidade"}]}""");
        Assert.Equal([FindingSeverity.High, FindingSeverity.Medium], r.Value!.Findings.Select(f => f.Severity));
        Assert.Equal(3, r.Value.Findings[0].Line);
        Assert.False(OutputParsers.ParseReview("""{"verdict":"changes_requested","findings":[{"file":"a"}]}""").Ok);
        Assert.False(OutputParsers.ParseReview("""{"verdict":"ok"}""").Ok);
    }

    [Fact]
    public void Reassessment_accepts_valid_actions()
    {
        var id = Guid.NewGuid();
        var json = $$"""{"actions":[{"workItemId":"{{id}}","action":"remove_dependency","newDescription":"updated","newAcceptanceCriteria":"new criteria"}]}""";
        var r = OutputParsers.ParseReassessment(json, new HashSet<Guid> { id });
        Assert.True(r.Ok, r.Error);
        Assert.Single(r.Value!.Actions);
        Assert.Equal("remove_dependency", r.Value.Actions[0].Action);
    }

    [Fact]
    public void Reassessment_accepts_block_action()
    {
        var id = Guid.NewGuid();
        var json = $$"""{"actions":[{"workItemId":"{{id}}","action":"block"}]}""";
        var r = OutputParsers.ParseReassessment(json, new HashSet<Guid> { id });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("block", r.Value!.Actions[0].Action);
    }

    [Fact]
    public void Reassessment_rejects_unknown_work_item_id()
    {
        var json = $$"""{"actions":[{"workItemId":"{{Guid.NewGuid()}}","action":"block"}]}""";
        var r = OutputParsers.ParseReassessment(json, new HashSet<Guid> { Guid.NewGuid() });
        Assert.False(r.Ok);
        Assert.Contains("inválido", r.Error);
    }

    [Fact]
    public void Reassessment_rejects_invalid_action()
    {
        var id = Guid.NewGuid();
        var json = $$"""{"actions":[{"workItemId":"{{id}}","action":"delete"}]}""";
        var r = OutputParsers.ParseReassessment(json, new HashSet<Guid> { id });
        Assert.False(r.Ok);
        Assert.Contains("remove_dependency", r.Error);
    }

    [Fact]
    public void Qa_verdict_must_match_the_criteria()
    {
        Assert.True(OutputParsers.ParseQa("""{"verdict":"passed","criteria":[{"criterion":"c","passed":true,"evidence":"e"}]}""").Value!.Passed);
        Assert.False(OutputParsers.ParseQa("""{"verdict":"passed","criteria":[{"criterion":"c","passed":false}]}""").Ok);
        Assert.False(OutputParsers.ParseQa("""{"verdict":"failed","criteria":[{"criterion":"c","passed":true}]}""").Ok);
        Assert.False(OutputParsers.ParseQa("""{"verdict":"failed"}""").Ok);
        Assert.True(OutputParsers.ParseQa("""{"verdict":"failed","criteria":[{"criterion":"c","passed":false,"evidence":"e"}]}""").Ok);
    }
}
