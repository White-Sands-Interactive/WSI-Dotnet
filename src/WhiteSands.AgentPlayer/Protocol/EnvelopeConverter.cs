using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhiteSands.AgentPlayer.Protocol;

/// <summary>
/// Reads and writes envelopes by the <c>type</c> field. Property order does not matter,
/// so messages from the Go platform decode the same as messages from this SDK.
/// </summary>
public sealed class EnvelopeConverter : JsonConverter<Envelope>
{
    public override Envelope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeProperty) &&
            !root.TryGetProperty("Type", out typeProperty))
        {
            throw new JsonException("Protocol message is missing type.");
        }

        var type = typeProperty.GetString() ?? "";
        var body = ProtocolJson.Body;
        return type switch
        {
            "hello" => Deserialize<HelloEnvelope>(root, body),
            "hello_ok" => Deserialize<HelloOkEnvelope>(root, body),
            "hello_err" => Deserialize<HelloErrEnvelope>(root, body),
            "start_session" => Deserialize<StartSessionEnvelope>(root, body),
            "session_started" => Deserialize<SessionStartedEnvelope>(root, body),
            "stop_session" => Deserialize<StopSessionEnvelope>(root, body),
            "session_stopped" => Deserialize<SessionStoppedEnvelope>(root, body),
            "mcp" => Deserialize<McpEnvelope>(root, body),
            "turn_result" => Deserialize<TurnResultEnvelope>(root, body),
            "session_event" => Deserialize<SessionEventEnvelope>(root, body),
            "ping" => Deserialize<PingEnvelope>(root, body),
            "pong" => Deserialize<PongEnvelope>(root, body),
            "resume_session" => Deserialize<ResumeSessionEnvelope>(root, body),
            "session_resumed" => Deserialize<SessionResumedEnvelope>(root, body),
            "resume_err" => Deserialize<ResumeErrEnvelope>(root, body),
            "error" => Deserialize<ErrorEnvelope>(root, body),
            _ => new UnknownEnvelope(type, root.Clone()),
        };
    }

    public override void Write(Utf8JsonWriter writer, Envelope value, JsonSerializerOptions options)
    {
        if (value is UnknownEnvelope unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }

        var json = JsonSerializer.SerializeToElement(value, value.GetType(), ProtocolJson.Body);
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        foreach (var property in json.EnumerateObject())
        {
            if (property.NameEquals("type") || property.NameEquals("Type"))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static T Deserialize<T>(JsonElement root, JsonSerializerOptions options) where T : Envelope
    {
        return root.Deserialize<T>(options) ?? throw new JsonException($"Could not read {typeof(T).Name}.");
    }
}
