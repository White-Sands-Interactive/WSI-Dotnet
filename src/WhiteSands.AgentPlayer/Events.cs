using System.Text.Json;

namespace WhiteSands.AgentPlayer;

/// <summary>Something that happened on a session, or on the platform connection.</summary>
public abstract record AgentEvent
{
    public abstract string Type { get; }
}

public sealed record ActionEvent(string SessionId, string Action, JsonElement Args) : AgentEvent
{
    public override string Type => "action";
}

public sealed record RetryEvent(string SessionId, string Action, JsonElement Args, string Error) : AgentEvent
{
    public override string Type => "retry";
}

public sealed record TurnFailedEvent(
    string SessionId,
    string Error,
    string? Action = null,
    JsonElement? Args = null) : AgentEvent
{
    public override string Type => "turnFailed";
}

public sealed record DisconnectedEvent : AgentEvent
{
    public override string Type => "disconnected";
}

public sealed record ReconnectedEvent : AgentEvent
{
    public override string Type => "reconnected";
}
