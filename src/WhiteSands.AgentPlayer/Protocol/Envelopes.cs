using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhiteSands.AgentPlayer.Protocol;

public abstract class Envelope
{
    [JsonIgnore]
    public abstract string Type { get; }
}

public sealed class HelloEnvelope : Envelope
{
    public override string Type => "hello";

    public string PlatformKey { get; set; } = "";

    public string ClientId { get; set; } = "";

    public int ProtocolVersion { get; set; }

    public string SdkVersion { get; set; } = "";
}

public sealed class HelloOkEnvelope : Envelope
{
    public override string Type => "hello_ok";

    public string AccountName { get; set; } = "";

    public int ProtocolVersion { get; set; }
}

public sealed class HelloErrEnvelope : Envelope
{
    public override string Type => "hello_err";

    public string Message { get; set; } = "";

    public string? Code { get; set; }
}

public sealed class StartSessionEnvelope : Envelope
{
    public override string Type => "start_session";

    public string SessionId { get; set; } = "";

    public string SessionToken { get; set; } = "";

    public string? Name { get; set; }

    public List<ToolDescriptor> Tools { get; set; } = new();

    public string Instructions { get; set; } = "";

    public string? Goal { get; set; }
}

public sealed class SessionStartedEnvelope : Envelope
{
    public override string Type => "session_started";

    public string SessionId { get; set; } = "";
}

public sealed class StopSessionEnvelope : Envelope
{
    public override string Type => "stop_session";

    public string SessionId { get; set; } = "";
}

public sealed class SessionStoppedEnvelope : Envelope
{
    public override string Type => "session_stopped";

    public string SessionId { get; set; } = "";
}

public sealed class McpEnvelope : Envelope
{
    public override string Type => "mcp";

    public string SessionId { get; set; } = "";

    public JsonElement Message { get; set; }
}

public sealed class TurnResultEnvelope : Envelope
{
    public override string Type => "turn_result";

    public string SessionId { get; set; } = "";

    public bool Ok { get; set; }

    public string? Error { get; set; }

    public string? Action { get; set; }

    public JsonElement? Args { get; set; }

    public JsonElement? Result { get; set; }

    public int? Attempts { get; set; }

    public List<AttemptRecord>? AttemptDetails { get; set; }

    public TokenUsage? Usage { get; set; }

    public bool ConnectionLost { get; set; }

    public TurnReport ToReport() => new()
    {
        Ok = Ok,
        Error = Error,
        Action = Action,
        Args = Args,
        Result = Result,
        Attempts = Attempts,
        AttemptDetails = AttemptDetails,
        Usage = Usage,
        ConnectionLost = ConnectionLost,
    };
}

public sealed class SessionEventEnvelope : Envelope
{
    public override string Type => "session_event";

    public string SessionId { get; set; } = "";

    public string Event { get; set; } = "";

    public string? Action { get; set; }

    public JsonElement? Args { get; set; }

    public string? Error { get; set; }
}

public sealed class PingEnvelope : Envelope
{
    public override string Type => "ping";

    public string Id { get; set; } = "";
}

public sealed class PongEnvelope : Envelope
{
    public override string Type => "pong";

    public string Id { get; set; } = "";
}

public sealed class ResumeSessionEnvelope : Envelope
{
    public override string Type => "resume_session";

    public string SessionId { get; set; } = "";

    public string SessionToken { get; set; } = "";
}

public sealed class SessionResumedEnvelope : Envelope
{
    public override string Type => "session_resumed";

    public string SessionId { get; set; } = "";
}

public sealed class ResumeErrEnvelope : Envelope
{
    public override string Type => "resume_err";

    public string SessionId { get; set; } = "";

    public string Message { get; set; } = "";
}

public sealed class ErrorEnvelope : Envelope
{
    public override string Type => "error";

    public string Message { get; set; } = "";

    public string? SessionId { get; set; }
}

/// <summary>A <c>type</c> this SDK does not know. Dispatch throws, matching <c>assertNever</c>.</summary>
public sealed class UnknownEnvelope : Envelope
{
    public UnknownEnvelope(string type, JsonElement raw)
    {
        Type = type;
        Raw = raw;
    }

    public override string Type { get; }

    public JsonElement Raw { get; }
}
