using System.Reflection;
using System.Text.Json;
using WhiteSands.AgentPlayer.Reflection;

namespace WhiteSands.AgentPlayer.Tests;

public class ReflectTests
{
    [Fact]
    public void ReadsParameterNamesFromAMethod()
    {
        var move = typeof(Sample).GetMethod(nameof(Sample.Move))!;
        Assert.Equal(new[] { "dx", "dy" }, move.GetParameters().Select(parameter => parameter.Name).ToArray());
        var chop = typeof(Sample).GetMethod(nameof(Sample.Chop))!;
        Assert.Empty(chop.GetParameters());
    }

    [Fact]
    public void PublishesSeeActionsAndWaitAndSkipsUnderscoreMethods()
    {
        var reflected = PlayerReflector.Reflect(new Sample());
        Assert.Equal("see", reflected.See.Name);
        Assert.Equal(new[] { "chop", "move", "poke" }, reflected.Actions.Select(action => action.Name).OrderBy(name => name).ToArray());
        Assert.Equal(new[] { "chop", "move", "poke", "see", "wait" }, reflected.Tools.Select(tool => tool.Name).OrderBy(name => name).ToArray());
        var move = reflected.Tools.Single(tool => tool.Name == "move");
        Assert.Equal(new[] { "dx", "dy" }, Required(move.InputSchema));
        Assert.Equal("integer", move.InputSchema.GetProperty("properties").GetProperty("dx").GetProperty("type").GetString());
        var poke = reflected.Tools.Single(tool => tool.Name == "poke");
        Assert.False(poke.InputSchema.GetProperty("properties").GetProperty("value").TryGetProperty("type", out _));
    }

    [Fact]
    public async Task InvokesMethodsWithNamedArgs()
    {
        var reflected = PlayerReflector.Reflect(new Sample());
        var move = reflected.Actions.Single(action => action.Name == "move");
        var result = await move.InvokeAsync(JsonSerializer.SerializeToElement(new { dx = 2, dy = 3 }));
        Assert.Equal(5, result);
    }

    [Fact]
    public void RequiresSee()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PlayerReflector.Reflect(new NoSee()));
        Assert.Contains("see", error.Message);
    }

    [Fact]
    public async Task UsesDeclaredTypesAndStillReflectsUndeclaredMethods()
    {
        var reflected = PlayerReflector.Reflect(new Typed());
        Assert.Equal("A typed player", reflected.Description);
        Assert.Equal(new[] { "move", "wave" }, reflected.Actions.Select(action => action.Name).OrderBy(name => name).ToArray());
        var move = reflected.Tools.Single(tool => tool.Name == "move");
        Assert.Equal("Step by dx", move.Description);
        Assert.Equal("integer", move.InputSchema.GetProperty("properties").GetProperty("dx").GetProperty("type").GetString());
        var action = reflected.Actions.Single(item => item.Name == "move");
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(() => action.InvokeAsync(JsonSerializer.SerializeToElement(new { dx = "no" })));
        Assert.Contains("dx", invalid.Message);
        Assert.Equal(4, await action.InvokeAsync(JsonSerializer.SerializeToElement(new { dx = 4 })));
    }

    [Fact]
    public void RejectsADeclaredActionThatIsNotAMethod()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PlayerReflector.Reflect(new Broken()));
        Assert.Contains("fly", error.Message);
    }

    private static string[] Required(JsonElement schema) =>
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray();

    private sealed class Sample
    {
        public object See() => new { Ok = true };

        public int Move(int dx, int dy) => dx + dy;

        public int Chop() => 1;

        public object Poke(object value) => value;

        public string _hidden() => "secret";
    }

    private sealed class NoSee
    {
        public int Move() => 1;
    }

    [AgentPlayer(Description = "A typed player")]
    private sealed class Typed
    {
        public object See() => new { Ok = true };

        [AgentAction("Step by dx")]
        public int Move(int dx) => dx;

        public string Wave() => "hi";
    }

    private sealed class Broken
    {
        public object See() => new { };

        [AgentAction("nope")]
        private void Fly()
        {
        }
    }
}
