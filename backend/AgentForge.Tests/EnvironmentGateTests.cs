using AgentForge.Orchestrator.Workflow;

namespace AgentForge.Tests;

public class EnvironmentGateTests
{
    [Fact]
    public void Work_beyond_the_limit_is_parked_and_comes_back_when_a_slot_opens()
    {
        var gate = new EnvironmentGate<string>();

        Assert.True(gate.TryEnter("a", 2, "t1"));
        Assert.True(gate.TryEnter("a", 2, "t2"));
        Assert.False(gate.TryEnter("a", 2, "t3"));
        Assert.False(gate.TryEnter("a", 2, "t4"));
        Assert.Equal(new EnvironmentSlotInfo("a", 2, 2, 2), gate.Snapshot().Single());

        var back = gate.Leave("a");

        Assert.Equal(["t3", "t4"], back);
        Assert.True(gate.TryEnter("a", 2, "t3"));
        Assert.False(gate.TryEnter("a", 2, "t4"));
    }

    [Fact]
    public void Environments_do_not_block_each_other_and_the_limit_can_change_on_the_fly()
    {
        var gate = new EnvironmentGate<string>();

        Assert.True(gate.TryEnter("a", 1, "a1"));
        Assert.False(gate.TryEnter("a", 1, "a2"));
        Assert.True(gate.TryEnter("b", 1, "b1"));
        Assert.True(gate.TryEnter("a", 2, "a2"));
    }

    [Fact]
    public void A_limit_below_one_is_treated_as_one_and_leaving_an_unknown_key_is_harmless()
    {
        var gate = new EnvironmentGate<string>();

        Assert.True(gate.TryEnter("a", 0, "t1"));
        Assert.False(gate.TryEnter("a", 0, "t2"));
        Assert.Empty(gate.Leave("nunca-usado"));
    }

    [Fact]
    public async Task Parking_and_leaving_concurrently_never_loses_work()
    {
        var gate = new EnvironmentGate<int>();
        var done = new System.Collections.Concurrent.ConcurrentBag<int>();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<int>(Enumerable.Range(0, 200));

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (done.Count < 200)
            {
                if (!queue.TryDequeue(out var work)) { await Task.Delay(1); continue; }
                if (!gate.TryEnter("a", 2, work)) continue;
                await Task.Yield();
                done.Add(work);
                foreach (var parked in gate.Leave("a")) queue.Enqueue(parked);
            }
        })).ToArray();

        await Task.WhenAny(Task.WhenAll(workers), Task.Delay(TimeSpan.FromSeconds(20)));

        Assert.Equal(200, done.Distinct().Count());
    }
}
