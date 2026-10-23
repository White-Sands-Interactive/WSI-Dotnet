using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhiteSands.AgentPlayer.Protocol;

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonSerializerOptions Body { get; } = CreateBody();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static JsonElement ToElement(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Undefined ? Parse("null") : element;
        }

        return JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), Body);
    }

    public static JsonElement EmptyObject() => Parse("{}");

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonSerializerOptions CreateBody() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = CreateBody();
        options.Converters.Add(new EnvelopeConverter());
        return options;
    }
}
