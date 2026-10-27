using WhiteSands.AgentPlayer;
using WhiteSands.AgentPlayer.Testing;

var solution = new[] { "goat", "none", "cabbage", "goat", "wolf", "none", "goat" };
const string Goal =
    "Get the wolf, the goat, and the cabbage onto the far bank. Never leave the wolf alone with the goat, or the goat alone with the cabbage.";

var useRemote = args.Contains("--remote");
var ferry = new Ferryman();
Console.WriteLine(ferry.Render());

var agents = useRemote
    ? new AgentServer(new AgentServerOptions
    {
        PlatformKey = Required("PLATFORM_KEY"),
    })
    : new AgentServer(new AgentServerOptions
    {
        Inline = TestPlatform.Create(solution.Select(passenger => ToolCallRequest.Call("cross", new { passenger })).ToArray()),
    });

var agent = await agents.StartAsync(ferry, new StartOptions { Name = "Ferryman", Goal = Goal });
var limit = useRemote ? 12 : solution.Length;
for (var turn = 1; turn <= limit && !ferry.Solved; turn++)
{
    try
    {
        var result = await agent.TurnAsync();
        Console.WriteLine($"\n{turn}. {result.Action}({result.Args.GetRawText()}) attempts={result.Attempts}");
        Console.WriteLine(ferry.Render());
    }
    catch (Exception ex)
    {
        var failed = ex as TurnFailedException;
        var attempts = failed == null ? "" : $" after {failed.Attempts.Count} attempt(s)";
        Console.WriteLine($"\n{turn}. turn failed{attempts}: {ex.Message}");
        Console.WriteLine(ferry.Render());
        break;
    }
}

await agent.StopAsync();
await agents.CloseAsync();
if (!ferry.Solved)
{
    throw new InvalidOperationException("The wolf, goat, and cabbage did not all reach the far bank");
}

Console.WriteLine("\nOK — everyone is on the far bank.");

static string Required(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrEmpty(value))
    {
        throw new InvalidOperationException($"{name} is required for --remote");
    }

    return value;
}

[AgentPlayer(Description = "A ferryman moving a wolf, a goat, and a cabbage across a river")]
file sealed class Ferryman
{
    private static readonly string[] Passengers = ["wolf", "goat", "cabbage"];
    private readonly List<string> _near = ["wolf", "goat", "cabbage"];
    private readonly List<string> _far = [];

    public string Boat { get; private set; } = "near";

    public bool Solved => Boat == "far" && _far.Count == Passengers.Length;

    public object See() => new
    {
        boat = Boat,
        near = Listed(_near),
        far = Listed(_far),
        solved = Solved,
        rule = "The boat carries you and one passenger. Without you there, the wolf eats the goat and the goat eats the cabbage. The wolf and the cabbage may be left together.",
    };

    [AgentAction("Cross to the other bank. passenger is someone on your current bank, or none to cross alone. Refuses a trip that would leave the wolf alone with the goat, or the goat alone with the cabbage.")]
    public object Cross(string passenger)
    {
        var from = Boat == "near" ? _near : _far;
        var to = Boat == "near" ? _far : _near;
        if (passenger != "none" && !from.Contains(passenger))
        {
            var other = Boat == "near" ? "far" : "near";
            throw new InvalidOperationException($"{passenger} is on the {other} bank. You are on the {Boat} bank.");
        }

        var leftBehind = from.Where(item => item != passenger).ToArray();
        var danger = UnsupervisedDanger(leftBehind);
        if (danger != null)
        {
            throw new InvalidOperationException(danger);
        }

        if (passenger != "none")
        {
            from.Remove(passenger);
            to.Add(passenger);
        }

        Boat = Boat == "near" ? "far" : "near";
        return new { boat = Boat, near = Listed(_near), far = Listed(_far) };
    }

    public string Render()
    {
        var boat = Boat == "near" ? "boat ~ ~ ~ ~" : "~ ~ ~ ~ boat";
        return $"near [{string.Join(" ", Listed(_near))}]  {boat}  far [{string.Join(" ", Listed(_far))}]";
    }

    private static string[] Listed(IReadOnlyList<string> items) =>
        Passengers.Where(items.Contains).ToArray();

    private static string? UnsupervisedDanger(IReadOnlyList<string> leftBehind)
    {
        var wolf = leftBehind.Contains("wolf");
        var goat = leftBehind.Contains("goat");
        var cabbage = leftBehind.Contains("cabbage");
        if (wolf && goat)
        {
            return "The wolf would eat the goat on the bank you are leaving";
        }

        if (goat && cabbage)
        {
            return "The goat would eat the cabbage on the bank you are leaving";
        }

        return null;
    }
}
