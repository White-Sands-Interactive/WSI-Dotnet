namespace WhiteSands.AgentPlayer.Protocol;

/// <summary>
/// Wire protocol between <see cref="AgentServer"/> and the platform. Version 1.
/// The platform's Go copy lives in White-Sands-Interactive/Platform, internal/protocol.
/// </summary>
public static class ProtocolConstants
{
    public const int ProtocolVersion = 1;

    public const string SdkVersion = "1.0.0";

    /// <summary>How long a disconnected session can be resumed. Shared by both sides.</summary>
    public static readonly TimeSpan SessionResumeGrace = TimeSpan.FromSeconds(30);

    /// <summary>MCP revision echoed during initialize. Matches the platform client.</summary>
    public const string McpProtocolVersion = "2025-06-18";

    public const string DefaultAgentInstructions =
        "You are a remote game agent. Tools are provided by the game.\n" +
        "On every turn: call wait first. It blocks until the game grants your turn and then returns the current view from see.\n" +
        "After wait returns, call exactly one action tool (not see, not wait).\n" +
        "The view's done list is what this character has already called in this session, oldest first. Use it when choosing the next action.\n" +
        "If an action tool returns an error, retry once with a corrected action. A second failure ends the turn as failed.";

    public const string SeeDescription =
        "Read the current game view for this player. Prefer wait, which returns this after the game grants a turn.";

    public const string WaitDescription =
        "Block until the game calls turn() for this session, then return the current see() view.";
}
