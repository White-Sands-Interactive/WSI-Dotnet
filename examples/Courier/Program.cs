using WhiteSands.AgentPlayer;
using WhiteSands.AgentPlayer.Testing;

var nia = new Courier(0);
var agents = new AgentServer(new AgentServerOptions
{
    Inline = TestPlatform.Create(new[]
    {
        ToolCallRequest.Call("walk", new { dx = 3 }),
        ToolCallRequest.Call("deliver"),
    }),
});

var agent = await agents.StartAsync(nia, new StartOptions
{
    Name = "Nia",
    Goal = "Deliver the parcel to the door",
});
var walked = await agent.TurnAsync();
var delivered = await agent.TurnAsync();
Console.WriteLine($"{walked.Action} {walked.Args.GetRawText()} -> {walked.Result.GetRawText()}");
Console.WriteLine($"{delivered.Action} -> {delivered.Result.GetRawText()}");
await agents.CloseAsync();

if (nia.X != 3 || !nia.Delivered)
{
    throw new InvalidOperationException("Courier did not deliver the parcel");
}

if (walked.Attempts != 1 || delivered.Action != "deliver")
{
    throw new InvalidOperationException("Unexpected turn results");
}

Console.WriteLine("OK — parcel delivered.");

[AgentPlayer(Description = "A courier carrying one parcel along a street")]
file sealed class Courier
{
    private const int Door = 3;

    public Courier(int x = 0) => X = x;

    public int X { get; private set; }

    public bool Delivered { get; private set; }

    public object See() => new { x = X, door = Door, carrying = !Delivered };

    [AgentAction("Move dx steps. Positive is toward the door.")]
    public object Walk(int dx)
    {
        var next = X + dx;
        if (next < 0 || next > Door)
        {
            throw new InvalidOperationException($"Cannot walk to {next}; the street is 0..{Door}");
        }

        X = next;
        return new { x = X };
    }

    [AgentAction("Hand over the parcel. Only works at x = 3.")]
    public object Deliver()
    {
        if (X != Door)
        {
            throw new InvalidOperationException($"The door is at {Door}, you are at {X}");
        }

        if (Delivered)
        {
            throw new InvalidOperationException("Already delivered");
        }

        Delivered = true;
        return new { delivered = true };
    }
}
