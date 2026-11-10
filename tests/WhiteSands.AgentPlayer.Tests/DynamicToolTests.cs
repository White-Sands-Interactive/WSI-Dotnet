using System.Text.Json;
using WhiteSands.AgentPlayer.Protocol;
using WhiteSands.AgentPlayer.Reflection;

namespace WhiteSands.AgentPlayer.Tests;

public class DynamicToolTests
{
    private static readonly JsonElement DxSchema = ProtocolJson.Parse(
        """{"type":"object","properties":{"dx":{"type":"integer"}},"required":["dx"]}""");

    [Fact]
    public async Task RegistersAToolAndDropsKeysThatAreNotInTheSchema()
    {
        var player = new Pawn();
        JsonElement seen = default;
        player.BindDynamic("leap", "", DxSchema, args =>
        {
            seen = args.Clone();
            return args.GetProperty("dx").GetInt32();
        });

        var reflected = PlayerReflector.Reflect(player);
        var leap = reflected.Actions.Single(action => action.Name == "leap");
        Assert.Equal("Action: leap", leap.Description);
        Assert.Equal(new[] { "see", "chop", "leap", "move", "wait" }, reflected.Tools.Select(tool => tool.Name).ToArray());
        Assert.Equal(4, Assert.IsType<int>(await leap.InvokeAsync(ProtocolJson.Parse("""{"dx":4,"extra":true}"""))));
        Assert.Equal(4, seen.GetProperty("dx").GetInt32());
        Assert.False(seen.TryGetProperty("extra", out _));
    }

    [Fact]
    public async Task ReplacesAToolRegisteredAgainAndAMethodOfTheSameName()
    {
        var player = new Pawn();
        player.BindDynamic("move", "first", DxSchema, _ => "first");
        player.BindDynamic("move", "second", DxSchema, _ => "second");
        var reflected = PlayerReflector.Reflect(player);
        Assert.Equal(1, reflected.Tools.Count(tool => tool.Name == "move"));
        var move = reflected.Actions.Single(action => action.Name == "move");
        Assert.Equal("second", move.Description);
        Assert.Equal("second", await move.InvokeAsync(ProtocolJson.Parse("""{"dx":1}""")));
    }

    [Fact]
    public async Task AllowsSeeAndUsesThatCallbackForTheView()
    {
        var player = new Pawn();
        player.BindDynamic("see", null, ProtocolJson.Parse("""{"type":"object","properties":{}}"""), _ => new { x = 9 });
        var reflected = PlayerReflector.Reflect(player);
        Assert.Equal("Action: see", reflected.See.Description);
        var view = await reflected.See.InvokeAsync(ProtocolJson.EmptyObject());
        Assert.Equal(9, view!.GetType().GetProperty("x")!.GetValue(view));
    }

    [Fact]
    public async Task SchemaFailureThrowsBeforeTheCallbackAndATaskResultIsUnwrapped()
    {
        var player = new Pawn();
        var called = false;
        player.BindDynamic("leap", "Jump", DxSchema, args =>
        {
            called = true;
            return Delayed(args);
        });
        var leap = PlayerReflector.Reflect(player).Actions.Single(action => action.Name == "leap");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => leap.InvokeAsync(ProtocolJson.Parse("""{"dx":"nope"}""")));
        Assert.Contains("dx", error.Message);
        Assert.False(called);
        Assert.Equal(5, Assert.IsType<int>(await leap.InvokeAsync(ProtocolJson.Parse("""{"dx":4}"""))));
        Assert.True(called);

        var posted = 0;
        PlayerInvoker invoker = call =>
        {
            posted++;
            return call();
        };
        var postedLeap = PlayerReflector.Reflect(player, invoker).Actions.Single(action => action.Name == "leap");
        Assert.Equal(8, Assert.IsType<int>(await postedLeap.InvokeAsync(ProtocolJson.Parse("""{"dx":7}"""))));
        Assert.Equal(1, posted);
    }

    [Fact]
    public void RejectsWaitAnEmptyNameAHiddenNameAMissingCallbackAndANonObjectSchema()
    {
        var player = new Pawn();
        var schema = ProtocolJson.Parse("""{"type":"object","properties":{}}""");
        Assert.Contains("wait", Assert.Throws<InvalidOperationException>(() => player.BindDynamic("wait", "nope", schema, _ => 1)).Message);
        Assert.Contains("required", Assert.Throws<InvalidOperationException>(() => player.BindDynamic("", "nope", schema, _ => 1)).Message);
        Assert.Contains("_", Assert.Throws<InvalidOperationException>(() => player.BindDynamic("_hidden", "nope", schema, _ => 1)).Message);
        Assert.Contains("callback", Assert.Throws<InvalidOperationException>(() => player.BindDynamic("leap", "nope", schema, null!)).Message);
        Assert.Contains("schema", Assert.Throws<InvalidOperationException>(() => player.BindDynamic("leap", "nope", ProtocolJson.Parse("1"), _ => 1)).Message);
    }

    [Fact]
    public async Task ChecksDeclaredTypesAndTreatsAMissingRequiredValueAsAFailure()
    {
        var player = new Pawn();
        var called = false;
        var schema = ProtocolJson.Parse(
            """
            {"type":"object","properties":{"name":{"type":"string"},"dx":{"type":"integer"},"scale":{"type":"number"},"ok":{"type":"boolean"},"path":{"type":"array","items":{"type":"integer"}},"where":{"type":"object","properties":{"x":{"type":"integer"}},"required":["x"]},"facing":{"type":"string","enum":["north","south"]}},"required":["name","dx"]}
            """);
        player.BindDynamic("leap", "Jump", schema, _ =>
        {
            called = true;
            return 1;
        });
        var leap = PlayerReflector.Reflect(player).Actions.Single(action => action.Name == "leap");
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => leap.InvokeAsync(ProtocolJson.Parse("""{"name":"ada"}""")));
        Assert.Contains("dx", missing.Message);
        Assert.False(called);
        await Assert.ThrowsAsync<InvalidOperationException>(() => leap.InvokeAsync(ProtocolJson.Parse("""{"name":"ada","dx":true}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => leap.InvokeAsync(ProtocolJson.Parse("""{"name":1,"dx":1}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => leap.InvokeAsync(ProtocolJson.Parse("""{"name":"ada","dx":1,"scale":"wide"}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => leap.InvokeAsync(ProtocolJson.Parse("""{"name":"ada","dx":1,"ok":"yes"}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => leap.InvokeAsync(ProtocolJson.Parse("""{"name":"ada","dx":1,"path":[1,"no"]}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => leap.InvokeAsync(ProtocolJson.Parse("""{"name":"ada","dx":1,"where":{"y":2}}""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => leap.InvokeAsync(ProtocolJson.Parse("""{"name":"ada","dx":1,"facing":"east"}""")));
        Assert.False(called);
        Assert.Equal(1, await leap.InvokeAsync(ProtocolJson.Parse(
            """{"name":"ada","dx":2,"scale":1.5,"ok":true,"path":[1],"where":{"x":3,"extra":true},"facing":"north","spare":1}""")));
        Assert.True(called);
    }

    private static async Task<object?> Delayed(JsonElement args)
    {
        await Task.Yield();
        return args.GetProperty("dx").GetInt32() + 1;
    }

    private sealed class Pawn
    {
        public object See() => new { x = 0 };

        public object Move(int dx) => new { dx };

        public object Chop() => new { };
    }
}
