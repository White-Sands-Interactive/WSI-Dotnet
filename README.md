# WSI Agent Player for C#

The library starts a remote game agent from a player class. The game calls `StartAsync` and `TurnAsync`. The SDK publishes that class as tools. The model runs on the platform, so the game process never holds the model key.

This is the C# port of [`@whitesands/agent-player`](https://github.com/White-Sands-Interactive/WSI-Node). Both speak protocol version 1.

The library also provides:

- A player class turned into tools, with argument schemas taken from the method signature.
- One action per turn, and one retry when that action throws.
- An offline loop for tests, with no socket and no platform key.
- A log of what each character has already called. The core package keeps that log in memory.

## Installation

Game projects install the core package:

```sh
dotnet add package WhiteSands.AgentPlayer
```

That package is the SDK. It targets `net8.0` and `netstandard2.1` (Unity 2021.2 or newer). `ClientWebSocket` works in Unity standalone and mobile players. It does not work in WebGL.

`WhiteSands.AgentPlayer.Sqlite` is a separate package and is not part of the SDK. Unity cannot load the native SQLite library it ships, so a Unity game should stay on the core package. The in-memory log is what the model reads during play: each session sees only its own calls, and a new `StartAsync` starts empty. The SQLite package exists for a .NET server or console host that wants the same `.wsi/agent-memory.sqlite` file the Node SDK writes, so the rows are still there after the process restarts.

Unity strips reflected methods unless you preserve them. Keep the player class, or add a `link.xml` that preserves your player assembly.

## Usage

Set the platform key, then start one agent per character. `TurnAsync` grants that character one chance to act and returns what they did.

```sh
export WSI_PLATFORM_KEY=pk_...
```

The SDK connects to `wss://api.whitesandsint.com`. That address is part of the library.

```csharp
await using var agents = new AgentServer();
var ada = await agents.StartAsync(new Villager(world, "Ada"), new StartOptions
{
    Name = "Ada",
    Goal = "Stand on the tree and chop it",
});

var result = await ada.TurnAsync();
// result.Action, result.Args, result.Result, result.Attempts, result.Usage
```

`new AgentServer(new AgentServerOptions { PlatformKey = "..." })` overrides `WSI_PLATFORM_KEY`.

### The player class

`See` is required. It returns the JSON the model reads at the start of the turn. Every other public method is an action the model may call. Names that start with `_`, methods marked `[AgentIgnore]`, and the constructor are left off the tool list. `Move` is published as `move`.

Throw to refuse an action. The platform asks the model once more. A second failure throws `TurnFailedException` from `TurnAsync`.

```csharp
public sealed class Villager
{
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Wood { get; private set; }

    public object See() => new
    {
        x = X,
        y = Y,
        wood = Wood,
        tile = world.Get(X, Y),
    };

    public object Move(int dx, int dy)
    {
        var nextX = X + dx;
        var nextY = Y + dy;
        if (!world.InBounds(nextX, nextY))
        {
            throw new InvalidOperationException($"Cannot move to {nextX},{nextY}");
        }

        X = nextX;
        Y = nextY;
        return new { x = X, y = Y };
    }

    public object Chop()
    {
        if (world.Get(X, Y) != "T")
        {
            throw new InvalidOperationException($"No tree at {X},{Y}");
        }

        Wood += 1;
        return new { wood = Wood };
    }
}
```

A method with no `[AgentAction]` is still a tool. `object` and `JsonElement` parameters accept any JSON value. `int` and `long` are integers. `double` is a number. A value that does not match the parameter type counts as a failed action and uses the one retry.

### Declaring arguments

`[AgentPlayer]` and `[AgentAction]` add the description the model sees. The schema comes from the parameters, so a string passed to `int dx` fails before your method runs.

```csharp
[AgentPlayer(Description = "A villager who gathers wood")]
public sealed class Villager
{
    public object See() => new { x = X, y = Y };

    [AgentAction("Step by dx and dy. Stay inside the map.")]
    public object Move(int dx, int dy) => new { x = X + dx, y = Y + dy };

    [AgentAction("Chop the tree on your tile")]
    public object Chop() => new { wood = Wood };
}
```

Methods that return `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>` are awaited. A method that returns `void` or `null` is reported as `{ "ok": true }`. Return values are serialized as camelCase JSON.

### Unity main thread

Player methods run on the `SynchronizationContext` captured when `AgentServer` is constructed. In Unity that is the main thread, so `See` and your actions can touch the scene. Set `AgentServerOptions.InvokeOn` to choose a different context, or set `UseSynchronizationContext` to false to run on the caller.

Do not block the main thread on `TurnAsync(...).GetAwaiter().GetResult()`. The turn needs that thread to run `See`, and the wait deadlocks. Use a coroutine or `async` method instead.

### Turns

`TurnAsync` does not choose the action. It unblocks that session's `wait`. The platform reads `See`, calls one action, and reports back.

```csharp
var result = await ada.TurnAsync(
    new TurnOptions { Timeout = TimeSpan.FromSeconds(30) },
    cancellationToken);
```

| Field | Meaning |
| --- | --- |
| `Action` | Tool the model called |
| `Args` | Arguments it passed |
| `Result` | What your method returned |
| `Attempts` | `1`, or `2` when the first call failed and the retry succeeded |
| `Usage` | `PromptTokens` and `CompletionTokens` for that turn |

The default timeout is 60 seconds. Pass a `CancellationToken` to abort the wait. `handle.StopAsync()` ends that session. `agents.CloseAsync()`, also run by `await using`, stops every session on the server. The platform process keeps running.

### Errors

| Exception | When |
| --- | --- |
| `PlatformAuthException` | The platform rejected the key |
| `ProtocolVersionException` | This SDK and the platform disagree on the protocol |
| `TurnFailedException` | The action and the retry both failed. `Attempts` lists both calls |
| `TurnTimeoutException` | The platform did not finish within the timeout |
| `ConnectionLostException` | The socket dropped and the session could not be resumed |

The SDK reconnects with backoff. A disconnected session can be resumed for 30 seconds.

```csharp
try
{
    await ada.TurnAsync();
}
catch (TurnFailedException ex)
{
    Console.WriteLine(ex.Attempts.Count);
    throw;
}
```

### Several characters

One `AgentServer` hosts every character. Each `StartAsync` is its own session.

```csharp
var ada = await agents.StartAsync(new Villager(world, "Ada"), new StartOptions { Name = "Ada", Goal = "Chop the tree" });
var bea = await agents.StartAsync(new Villager(world, "Bea"), new StartOptions { Name = "Bea", Goal = "Carry wood home" });

await ada.TurnAsync();
await bea.TurnAsync();
```

The game decides whose turn it is by calling `TurnAsync` on that handle.

### Events

`agents.On` hears every session. `handle.On` hears one session. Both return an `IDisposable` that unsubscribes.

```csharp
using var stop = ada.On(evt =>
{
    if (evt is ActionEvent action) Console.WriteLine($"{action.Action} {action.Args}");
    if (evt is RetryEvent retry) Console.WriteLine($"{retry.Action} {retry.Error}");
    if (evt is TurnFailedEvent failed) Console.WriteLine(failed.Error);
});
```

Connection events are `DisconnectedEvent` and `ReconnectedEvent`. Those have no session id.

### Action history

Every `StartAsync` records that character's calls. The next `See` view includes `done`, oldest first, up to the last 50 calls for that session:

```json
{
  "x": 1,
  "y": 0,
  "done": [
    { "action": "move", "args": { "dx": 1, "dy": 0 }, "ok": true }
  ]
}
```

A refused call is `{ "ok": false, "error": "..." }`. A new `StartAsync` has an empty `done` list. The core package stores this in memory for the life of the `AgentServer`. Two characters on one server share that log and see only their own rows. The name on each row is the `Name` passed to `StartAsync`, or the class name. `DisableMemory = true` turns the log off. The view then has no `done` field.

A .NET host that is not a Unity player can persist the same rows with the separate `WhiteSands.AgentPlayer.Sqlite` package. The file is created on the first `StartAsync`. Deleting it is safe. The default path is `.wsi/agent-memory.sqlite`.

```csharp
var options = new AgentServerOptions();
options.UseSqliteMemory("saves/agents.sqlite");
var agents = new AgentServer(options);
```

### Goal and instructions

`Goal` is added to the model prompt for that character. `Instructions` on `StartAsync` is appended to the default prompt. `Instructions` on `AgentServer` replaces the default prompt for every character on that server.

### Testing without a platform

`WhiteSands.AgentPlayer.Testing.TestPlatform` runs the same wait, act, and retry loop in memory. The script is the list of tool calls, in order. No key and no socket.

```csharp
var agents = new AgentServer(new AgentServerOptions
{
    Inline = TestPlatform.Create(new[]
    {
        ToolCallRequest.Call("move", new { dx = 1, dy = 0 }),
        ToolCallRequest.Call("chop"),
    }),
});
```

### Where each piece runs

| | Game process | Platform |
| --- | --- | --- |
| World and player methods | yes | |
| Tools (`see`, actions, `wait`) | yes | calls them |
| Whose turn it is | `TurnAsync` | |
| Which action to take | | the model |
| Model API key | | yes |

### Logging

Pass an `ILogger`. Omit it and nothing is written.

```csharp
var agents = new AgentServer(new AgentServerOptions
{
    Logger = loggerFactory.CreateLogger("Wsi"),
});
```

### Protocol

`WhiteSands.AgentPlayer.Protocol` holds the wire types and `ProtocolConstants.ProtocolVersion`. Game code uses `AgentServer` and does not need the protocol types.

## This repository

| Path | Role |
| --- | --- |
| `src/WhiteSands.AgentPlayer` | The SDK. Games, including Unity, use this package |
| `src/WhiteSands.AgentPlayer.Sqlite` | Separate package for a .NET host that wants the action log on disk. Not part of the SDK |
| `examples/Forest` | Two villagers |
| `examples/Courier` | A typed action and a goal |
| `examples/Ferry` | One passenger chosen each turn |

```sh
dotnet test
dotnet run --project examples/Courier
dotnet run --project examples/Ferry
dotnet run --project examples/Forest
```

Each example replays a script. Add `-- --remote` to talk to `wss://api.whitesandsint.com`:

```sh
PLATFORM_KEY=pk_... dotnet run --project examples/Forest -- --remote
```

Releases are recorded in [CHANGELOG.md](CHANGELOG.md).
