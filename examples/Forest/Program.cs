using WhiteSands.AgentPlayer;
using WhiteSands.AgentPlayer.Testing;

var useRemote = args.Contains("--remote");
var world = new Forest();
var ada = new Villager(world, "Ada", 0, 2);
var bea = new Villager(world, "Bea", 4, 4);

Console.WriteLine("Before:");
Console.WriteLine(world.Render());
Console.WriteLine($"Ada at ({ada.X},{ada.Y}) wood={ada.Wood}");

var agents = useRemote
    ? new AgentServer(new AgentServerOptions
    {
        PlatformKey = Required("PLATFORM_KEY"),
    })
    : new AgentServer(new AgentServerOptions
    {
        Inline = TestPlatform.Create(new[]
        {
            ToolCallRequest.Call("move", new { dx = 2, dy = 0 }),
            ToolCallRequest.Call("chop"),
            ToolCallRequest.Call("move", new { dx = -1, dy = 0 }),
        }),
    });

var adaAgent = await agents.StartAsync(ada, new StartOptions { Goal = "Stand on the tree and chop it" });
var beaAgent = await agents.StartAsync(bea);

var moved = await adaAgent.TurnAsync();
Console.WriteLine($"\nAda {moved.Action}({moved.Args.GetRawText()})");
Console.WriteLine($"Ada at ({ada.X},{ada.Y}) tile={world.Get(ada.X, ada.Y)} wood={ada.Wood}");

var chopped = await adaAgent.TurnAsync();
Console.WriteLine($"\nAda {chopped.Action}({chopped.Args.GetRawText()})");
Console.WriteLine(world.Render());
Console.WriteLine($"Ada at ({ada.X},{ada.Y}) wood={ada.Wood} treesLeft={world.TreesLeft()}");

try
{
    var beaTurn = await beaAgent.TurnAsync();
    Console.WriteLine($"\nBea {beaTurn.Action}({beaTurn.Args.GetRawText()})");
    Console.WriteLine($"Bea at ({bea.X},{bea.Y})");
}
catch (Exception ex)
{
    Console.WriteLine($"\nBea turn failed (model chose an illegal action): {ex.Message}");
    Console.WriteLine($"Bea still at ({bea.X},{bea.Y})");
}

await adaAgent.StopAsync();
await beaAgent.StopAsync();
await agents.CloseAsync();

if (!useRemote && (moved.Action != "move" || chopped.Action != "chop"))
{
    throw new InvalidOperationException("Scripted forest did not move then chop");
}

if (ada.X != 2 || ada.Y != 2 || ada.Wood != 1 || world.TreesLeft() != 0)
{
    throw new InvalidOperationException("Forest demo did not produce expected Ada state");
}

if (!useRemote && (bea.X != 3 || bea.Y != 4))
{
    throw new InvalidOperationException("Forest demo did not produce expected Bea state");
}

Console.WriteLine("\nOK — Ada moved and chopped; tile and wood changed.");

static string Required(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrEmpty(value))
    {
        throw new InvalidOperationException($"{name} is required for --remote");
    }

    return value;
}

file sealed class Forest
{
    private readonly char[] _tiles;

    public Forest(int width = 5, int height = 5)
    {
        Width = width;
        Height = height;
        _tiles = Enumerable.Repeat('.', width * height).ToArray();
        Set(2, 2, 'T');
    }

    public int Width { get; }

    public int Height { get; }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public char Get(int x, int y)
    {
        if (!InBounds(x, y))
        {
            throw new InvalidOperationException($"Out of bounds: {x},{y}");
        }

        return _tiles[(y * Width) + x];
    }

    public void Set(int x, int y, char tile)
    {
        if (!InBounds(x, y))
        {
            throw new InvalidOperationException($"Out of bounds: {x},{y}");
        }

        _tiles[(y * Width) + x] = tile;
    }

    public int TreesLeft() => _tiles.Count(tile => tile == 'T');

    public string Render()
    {
        var rows = new List<string>();
        for (var y = 0; y < Height; y++)
        {
            var row = "";
            for (var x = 0; x < Width; x++)
            {
                row += Get(x, y);
            }

            rows.Add(row);
        }

        return string.Join("\n", rows);
    }
}

file sealed class Villager
{
    private readonly Forest _world;

    public Villager(Forest world, string name, int x = 0, int y = 0)
    {
        _world = world;
        Name = name;
        X = x;
        Y = y;
    }

    public string Name { get; }

    public int X { get; private set; }

    public int Y { get; private set; }

    public int Wood { get; private set; }

    public object See() => new
    {
        name = Name,
        x = X,
        y = Y,
        wood = Wood,
        tile = _world.Get(X, Y).ToString(),
        treesLeft = _world.TreesLeft(),
        bounds = new { minX = 0, minY = 0, maxX = _world.Width - 1, maxY = _world.Height - 1 },
        hint = "move(dx,dy) changes position by integer steps; stay in bounds. chop() only works on tile T.",
        map = _world.Render(),
    };

    public object Move(int dx, int dy)
    {
        var nx = X + dx;
        var ny = Y + dy;
        if (!_world.InBounds(nx, ny))
        {
            throw new InvalidOperationException($"Cannot move to {nx},{ny}");
        }

        X = nx;
        Y = ny;
        return new { x = X, y = Y, tile = _world.Get(X, Y).ToString() };
    }

    public object Chop()
    {
        if (_world.Get(X, Y) != 'T')
        {
            throw new InvalidOperationException($"No tree at {X},{Y}");
        }

        _world.Set(X, Y, '.');
        Wood += 1;
        return new { wood = Wood, treesLeft = _world.TreesLeft() };
    }

    public string _secret() => "nope";
}
