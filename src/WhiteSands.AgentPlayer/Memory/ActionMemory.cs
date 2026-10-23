using System.Text.Json;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Memory;

public sealed class DoneAction
{
    public DoneAction(string action, JsonElement args, bool ok, string? error = null)
    {
        Action = action;
        Args = args;
        Ok = ok;
        Error = error;
    }

    public string Action { get; }

    public JsonElement Args { get; }

    public bool Ok { get; }

    public string? Error { get; }
}

/// <summary>
/// Log of actions one game process has already called.
/// Each session reads only its own rows, so a new run starts with an empty list.
/// </summary>
public interface IActionMemory : IDisposable
{
    void Record(string sessionId, string character, DoneAction entry);

    /// <summary>Oldest first, capped to the most recent actions for this session.</summary>
    IReadOnlyList<DoneAction> Done(string sessionId);
}

public static class ActionMemory
{
    public const int MaxDone = 50;

    /// <summary>Attach this session's action log to a see() payload.</summary>
    public static JsonElement ViewWithDone(JsonElement view, IReadOnlyList<DoneAction> done)
    {
        var doneNode = JsonSerializer.SerializeToNode(done, ProtocolJson.Body);
        if (view.ValueKind == JsonValueKind.Object)
        {
            var obj = JsonNodeClone(view).AsObject();
            obj["done"] = doneNode;
            return JsonSerializer.SerializeToElement(obj);
        }

        var wrapped = new System.Text.Json.Nodes.JsonObject
        {
            ["view"] = view.ValueKind == JsonValueKind.Null || view.ValueKind == JsonValueKind.Undefined
                ? null
                : JsonNodeClone(view),
            ["done"] = doneNode,
        };
        return JsonSerializer.SerializeToElement(wrapped);
    }

    private static System.Text.Json.Nodes.JsonNode JsonNodeClone(JsonElement view)
    {
        return System.Text.Json.Nodes.JsonNode.Parse(view.GetRawText())!;
    }
}
