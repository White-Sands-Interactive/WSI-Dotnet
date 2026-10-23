using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhiteSands.AgentPlayer.Mcp;

internal static class JsonRpc
{
    public static JsonElement Response(JsonElement id, JsonNode result)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = result,
        };
        return JsonSerializer.SerializeToElement(message);
    }

    public static JsonElement Error(JsonElement id, int code, string message)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        if (id.ValueKind != JsonValueKind.Undefined)
        {
            body["id"] = JsonNode.Parse(id.GetRawText());
        }

        return JsonSerializer.SerializeToElement(body);
    }

    public static JsonElement Request(int id, string method, JsonNode? parameters)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters != null)
        {
            message["params"] = parameters;
        }

        return JsonSerializer.SerializeToElement(message);
    }

    public static JsonElement Notify(string method, JsonNode? parameters)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (parameters != null)
        {
            message["params"] = parameters;
        }

        return JsonSerializer.SerializeToElement(message);
    }

    public static string IdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.GetRawText(),
        JsonValueKind.String => id.GetString() ?? "",
        _ => id.GetRawText(),
    };

    public static string NumberId(int id) => id.ToString(CultureInfo.InvariantCulture);
}
