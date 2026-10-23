using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Tests;

public class TurnGateTests
{
    [Fact]
    public async Task PairsWaitWithTurn()
    {
        var gate = new TurnGate();
        var events = new List<string>();
        var waiting = gate.WaitForTurnAsync().ContinueWith(_ => events.Add("wait-released"), TaskScheduler.Default);
        await Task.Yield();
        Assert.Empty(events);

        var turning = gate.RunTurnAsync(null, CancellationToken.None).ContinueWith(task =>
        {
            events.Add(task.Result.Ok ? "turn-ok" : "turn-fail");
            return task.Result;
        }, TaskScheduler.Default);

        await waiting;
        Assert.Equal(new[] { "wait-released" }, events);

        gate.CompleteTurn(new TurnReport { Ok = true });
        await turning;
        Assert.Equal(new[] { "wait-released", "turn-ok" }, events);
    }
}
