using System.Text.Json;

namespace WhiteSands.AgentPlayer.Protocol;

public sealed class ToolDescriptor
{
    public ToolDescriptor(string name, string description, JsonElement inputSchema)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement InputSchema { get; }
}

public sealed class TokenUsage
{
    public TokenUsage(int promptTokens, int completionTokens)
    {
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
    }

    public int PromptTokens { get; }

    public int CompletionTokens { get; }
}

public sealed class AttemptRecord
{
    public AttemptRecord(string action, JsonElement args, string error)
    {
        Action = action;
        Args = args;
        Error = error;
    }

    public string Action { get; }

    public JsonElement Args { get; }

    public string Error { get; }
}

/// <summary>Outcome of one turn. The platform sends this as <c>turn_result</c>.</summary>
public sealed class TurnReport
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public string? Action { get; set; }

    public JsonElement? Args { get; set; }

    public JsonElement? Result { get; set; }

    public int? Attempts { get; set; }

    public List<AttemptRecord>? AttemptDetails { get; set; }

    public TokenUsage? Usage { get; set; }

    public bool ConnectionLost { get; set; }
}
