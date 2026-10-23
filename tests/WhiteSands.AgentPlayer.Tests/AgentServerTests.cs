using System.Text.Json;
using WhiteSands.AgentPlayer.Protocol;
using WhiteSands.AgentPlayer.Testing;

namespace WhiteSands.AgentPlayer.Tests;

public class AgentServerTests
{
    [Fact]
    public async Task ReturnsTheActionTakenByAScriptedModel()
    {
        var world = new TinyWorld();
        var events = new List<AgentEvent>();
        var agents = new AgentServer(new AgentServerOptions
        {
            Inline = TestPlatform.Create(new[] { ToolCallRequest.Call("move", new { dx = 3 }) }),
            OnEvent = events.Add,
        });
        var agent = await agents.StartAsync(world);
        var result = await agent.TurnAsync();
        Assert.Equal("move", result.Action);
        Assert.Equal(3, result.Args.GetProperty("dx").GetInt32());
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, result.Usage.PromptTokens);
        Assert.Equal(3, world.X);
        Assert.Contains(events, evt => evt is ActionEvent action && action.Action == "move");
        await agents.CloseAsync();
    }

    [Fact]
    public async Task RetriesOnceAfterARejectedAction()
    {
        var world = new TinyWorld();
        var agents = new AgentServer(new AgentServerOptions
        {
            Inline = TestPlatform.Create(new[]
            {
                ToolCallRequest.Call("chop"),
                ToolCallRequest.Call("chop"),
            }),
        });
        var agent = await agents.StartAsync(world);
        var result = await agent.TurnAsync();
        Assert.Equal(2, result.Attempts);
        Assert.Equal("chop", result.Action);
        Assert.Equal(1, world.Wood);
        await agents.CloseAsync();
    }

    [Fact]
    public async Task ThrowsTurnFailedExceptionWithBothAttempts()
    {
        var world = new TinyWorld();
        var agents = new AgentServer(new AgentServerOptions
        {
            Inline = TestPlatform.Create(new[]
            {
                ToolCallRequest.Call("bad"),
                ToolCallRequest.Call("bad"),
            }),
        });
        var agent = await agents.StartAsync(world);
        var error = await Assert.ThrowsAsync<TurnFailedException>(() => agent.TurnAsync());
        Assert.Equal("always bad", error.Message);
        Assert.Equal(2, error.Attempts.Count);
        Assert.Equal("bad", error.Attempts[0].Action);
        Assert.Equal("always bad", error.Attempts[0].Error);
        Assert.Equal("bad", error.Attempts[1].Action);
        Assert.Equal("always bad", error.Attempts[1].Error);
        await agents.CloseAsync();
    }

    [Fact]
    public async Task RunsTwoSessionsOnOneAgentServer()
    {
        var a = new TinyWorld();
        var b = new TinyWorld();
        var agents = new AgentServer(new AgentServerOptions
        {
            Inline = TestPlatform.Create(new[]
            {
                ToolCallRequest.Call("move", new { dx = 1 }),
                ToolCallRequest.Call("move", new { dx = 5 }),
            }),
        });
        var agentA = await agents.StartAsync(a);
        var agentB = await agents.StartAsync(b);
        await agentA.TurnAsync();
        await agentB.TurnAsync();
        Assert.Equal(1, a.X);
        Assert.Equal(5, b.X);
        await agents.CloseAsync();
    }

    [Fact]
    public async Task RejectsATurnThatExceedsTheTimeout()
    {
        var agents = new AgentServer(new AgentServerOptions
        {
            Inline = new IdlePlatform(),
            TurnTimeout = TimeSpan.FromMilliseconds(50),
        });
        var agent = await agents.StartAsync(new TinyWorld());
        await Assert.ThrowsAsync<TurnTimeoutException>(() => agent.TurnAsync());
        await agents.CloseAsync();
    }

    [Fact]
    public async Task PassesGoalAndExtraInstructionsToThePlatform()
    {
        string goal = "";
        string instructions = "";
        var inline = new CapturePlatform(request =>
        {
            goal = request.Goal ?? "";
            instructions = request.Instructions;
        });
        var agents = new AgentServer(new AgentServerOptions { Inline = inline });
        await agents.StartAsync(new TinyWorld(), new StartOptions
        {
            Goal = "Collect wood",
            Instructions = "Stay calm.",
        });
        Assert.Equal("Collect wood", goal);
        Assert.Contains("Stay calm.", instructions);
        Assert.Contains("call wait first", instructions);
        Assert.Contains("done list", instructions);
        await agents.CloseAsync();
    }

    [Fact]
    public async Task TreatsASchemaFailureAsAFailedActionAndRetries()
    {
        var world = new Gated();
        var agents = new AgentServer(new AgentServerOptions
        {
            Inline = TestPlatform.Create(new[]
            {
                ToolCallRequest.Call("move", new { dx = "nope" }),
                ToolCallRequest.Call("move", new { dx = 2 }),
            }),
        });
        var agent = await agents.StartAsync(world);
        var result = await agent.TurnAsync();
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, world.X);
        await agents.CloseAsync();
    }

    [Fact]
    public void ReadsThePlatformKeyFromTheEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("WSI_PLATFORM_KEY");
        Environment.SetEnvironmentVariable("WSI_PLATFORM_KEY", null);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => new AgentServer());
            Assert.Contains("WSI_PLATFORM_KEY", error.Message);
            Environment.SetEnvironmentVariable("WSI_PLATFORM_KEY", "pk_test");
            var agents = new AgentServer(new AgentServerOptions { Inline = new IdlePlatform() });
            Assert.IsType<AgentServer>(agents);
        }
        finally
        {
            Environment.SetEnvironmentVariable("WSI_PLATFORM_KEY", previous);
        }
    }

    [Fact]
    public async Task ShowsThisCharactersEarlierActionsOnTheNextView()
    {
        var views = new List<string>();
        var directory = Directory.CreateTempSubdirectory("wsi-agent-");
        var options = new AgentServerOptions
        {
            Inline = TestPlatform.Create(new RecordingModel(views, new[]
            {
                ToolCallRequest.Call("move", new { dx = 1 }),
                ToolCallRequest.Call("move", new { dx = 4 }),
            })),
        };
        options.UseSqliteMemory(Path.Combine(directory.FullName, "memory.sqlite"));
        var agents = new AgentServer(options);
        var world = new TinyWorld();
        var agent = await agents.StartAsync(world, new StartOptions { Name = "Ada" });
        await agent.TurnAsync();
        await agent.TurnAsync();
        Assert.Equal(0, DoneFromPrompt(views[0]).GetArrayLength());
        var done = DoneFromPrompt(views[1]);
        Assert.Equal(1, done.GetArrayLength());
        Assert.Equal("move", done[0].GetProperty("action").GetString());
        Assert.Equal(1, done[0].GetProperty("args").GetProperty("dx").GetInt32());
        Assert.True(done[0].GetProperty("ok").GetBoolean());
        Assert.Equal(5, world.X);
        await agents.CloseAsync();
    }

    private static JsonElement DoneFromPrompt(string prompt)
    {
        var start = prompt.IndexOf('{');
        var end = prompt.LastIndexOf('}');
        using var document = JsonDocument.Parse(prompt.Substring(start, end - start + 1));
        return document.RootElement.GetProperty("done").Clone();
    }

    private sealed class TinyWorld
    {
        private bool _failOnce = true;

        public int X { get; private set; }

        public int Wood { get; private set; }

        public object See() => new { x = X, wood = Wood };

        public object Move(int dx)
        {
            X += dx;
            return new { x = X };
        }

        public object Chop()
        {
            if (_failOnce)
            {
                _failOnce = false;
                throw new InvalidOperationException("axe slipped");
            }

            Wood += 1;
            return new { wood = Wood };
        }

        public void Bad() => throw new InvalidOperationException("always bad");
    }

    private sealed class Gated
    {
        public int X { get; private set; }

        public object See() => new { x = X };

        public object Move(int dx)
        {
            X += dx;
            return new { x = X };
        }
    }

    private sealed class IdlePlatform : IInlinePlatform
    {
        public Task StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CapturePlatform : IInlinePlatform
    {
        private readonly Action<StartSessionRequest> _capture;

        public CapturePlatform(Action<StartSessionRequest> capture) => _capture = capture;

        public Task StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
        {
            _capture(request);
            return Task.CompletedTask;
        }

        public Task StopSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingModel : IModelClient
    {
        private readonly List<string> _views;
        private readonly IReadOnlyList<ToolCallRequest> _script;
        private int _index;

        public RecordingModel(List<string> views, IReadOnlyList<ToolCallRequest> script)
        {
            _views = views;
            _script = script;
        }

        public Task<ModelDecision> DecideAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            var prompt = request.Messages.OfType<UserModelMessage>().FirstOrDefault();
            if (prompt != null)
            {
                _views.Add(prompt.Content);
            }

            var call = _index < _script.Count ? _script[_index] : ToolCallRequest.Call("wait");
            _index += 1;
            return Task.FromResult(new ModelDecision(call, new TokenUsage(1, 1)));
        }
    }
}
