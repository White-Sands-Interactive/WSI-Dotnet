using System.Text.Json;
using WhiteSands.AgentPlayer.Mcp;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Testing;

public sealed class ToolCallRequest
{
    public ToolCallRequest(string name, JsonElement arguments)
    {
        Name = name;
        Arguments = arguments.ValueKind == JsonValueKind.Object ? arguments : ProtocolJson.EmptyObject();
    }

    public string Name { get; }

    public JsonElement Arguments { get; }

    public static ToolCallRequest Call(string name, object? arguments = null) =>
        new(name, arguments == null ? ProtocolJson.EmptyObject() : ProtocolJson.ToElement(arguments));
}

public abstract record ModelMessage
{
    public abstract string Role { get; }
}

public sealed record SystemModelMessage(string Content) : ModelMessage
{
    public override string Role => "system";
}

public sealed record UserModelMessage(string Content) : ModelMessage
{
    public override string Role => "user";
}

public sealed record AssistantModelMessage(string Content) : ModelMessage
{
    public override string Role => "assistant";
}

public sealed record ToolModelMessage(string Name, string Content, bool IsError = false) : ModelMessage
{
    public override string Role => "tool";
}

public sealed class ModelTool
{
    public ModelTool(string name, string description, JsonElement inputSchema)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement InputSchema { get; }
}

public sealed class ModelDecision
{
    public ModelDecision(ToolCallRequest toolCall, TokenUsage usage)
    {
        ToolCall = toolCall;
        Usage = usage;
    }

    public ToolCallRequest ToolCall { get; }

    public TokenUsage Usage { get; }
}

public sealed class ModelRequest
{
    public ModelRequest(string instructions, IReadOnlyList<ModelTool> tools, IReadOnlyList<ModelMessage> messages)
    {
        Instructions = instructions;
        Tools = tools;
        Messages = messages;
    }

    public string Instructions { get; }

    public IReadOnlyList<ModelTool> Tools { get; }

    public IReadOnlyList<ModelMessage> Messages { get; }
}

public interface IModelClient
{
    /// <summary>Ask the model to choose exactly one tool call.</summary>
    Task<ModelDecision> DecideAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Scripted model for tests: returns predetermined tool calls in order.
/// When the queue is exhausted, always returns wait.
/// </summary>
public sealed class ScriptedModel : IModelClient
{
    private readonly IReadOnlyList<ToolCallRequest> _script;
    private readonly object _gate = new();
    private int _index;

    public ScriptedModel(IReadOnlyList<ToolCallRequest> script)
    {
        _script = script;
    }

    public Task<ModelDecision> DecideAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ToolCallRequest call;
        lock (_gate)
        {
            call = _index < _script.Count
                ? _script[_index]
                : ToolCallRequest.Call("wait");
            _index += 1;
        }

        return Task.FromResult(new ModelDecision(call, new TokenUsage(1, 1)));
    }
}
