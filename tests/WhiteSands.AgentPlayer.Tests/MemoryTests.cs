using System.Text.Json;
using WhiteSands.AgentPlayer.Memory;
using WhiteSands.AgentPlayer.Protocol;
using WhiteSands.AgentPlayer.Sqlite;

namespace WhiteSands.AgentPlayer.Tests;

public class MemoryTests
{
    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public void ReturnsOneSessionsActionsOldestFirst(string kind)
    {
        using var memory = Create(kind);
        memory.Record("ada", "Ada", Action("cross", new { passenger = "goat" }, true));
        memory.Record("ada", "Ada", Action("cross", new { passenger = "wolf" }, false, "wrong bank"));
        memory.Record("bea", "Bea", Action("move", new { dx = 1 }, true));

        AssertDone(memory.Done("ada"), new (string Action, string Args, bool Ok, string? Error)[]
        {
            ("cross", """{"passenger":"goat"}""", true, null),
            ("cross", """{"passenger":"wolf"}""", false, "wrong bank"),
        });
        AssertDone(memory.Done("bea"), new (string Action, string Args, bool Ok, string? Error)[]
        {
            ("move", """{"dx":1}""", true, null),
        });
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public void KeepsTheMostRecentActionsOnTheView(string kind)
    {
        using var memory = Create(kind);
        for (var i = 0; i < 55; i++)
        {
            memory.Record("ada", "Ada", Action("move", new { dx = i }, true));
        }

        var done = memory.Done("ada");
        Assert.Equal(50, done.Count);
        Assert.Equal(5, done[0].Args.GetProperty("dx").GetInt32());
        Assert.Equal(54, done[49].Args.GetProperty("dx").GetInt32());
    }

    [Fact]
    public void AddsDoneOntoAnObjectView()
    {
        var objectView = ActionMemory.ViewWithDone(ProtocolJson.ToElement(new { boat = "near" }), Array.Empty<DoneAction>());
        Assert.Equal("near", objectView.GetProperty("boat").GetString());
        Assert.Equal(JsonValueKind.Array, objectView.GetProperty("done").ValueKind);
        Assert.Empty(objectView.GetProperty("done").EnumerateArray());

        var plain = ActionMemory.ViewWithDone(ProtocolJson.ToElement("plain"), Array.Empty<DoneAction>());
        Assert.Equal("plain", plain.GetProperty("view").GetString());
        Assert.Equal(JsonValueKind.Array, plain.GetProperty("done").ValueKind);
    }

    private static IActionMemory Create(string kind)
    {
        if (kind == "memory")
        {
            return new InMemoryActionMemory();
        }

        var directory = Directory.CreateTempSubdirectory("wsi-memory-");
        return new SqliteActionMemory(Path.Combine(directory.FullName, "memory.sqlite"));
    }

    private static DoneAction Action(string name, object args, bool ok, string? error = null) =>
        new(name, ProtocolJson.ToElement(args), ok, error);

    private static void AssertDone(
        IReadOnlyList<DoneAction> actual,
        IReadOnlyList<(string Action, string Args, bool Ok, string? Error)> expected)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Action, actual[i].Action);
            Assert.Equal(expected[i].Ok, actual[i].Ok);
            Assert.Equal(expected[i].Error, actual[i].Error);
            Assert.Equal(
                JsonSerializer.Serialize(JsonDocument.Parse(expected[i].Args).RootElement),
                JsonSerializer.Serialize(actual[i].Args));
        }
    }
}
