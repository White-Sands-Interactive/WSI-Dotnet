using System.Text.Json;
using System.Text.Json.Nodes;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Reflection;

/// <summary>
/// Checks a dynamic tool's arguments the same way a declared method is bound:
/// required values must be present, declared types must match, and other keys are dropped.
/// </summary>
internal static class ArgumentSchema
{
    public static JsonElement Bind(JsonElement schema, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            args = ProtocolJson.EmptyObject();
        }

        return FilterObject(schema, args);
    }

    private static JsonElement FilterObject(JsonElement schema, JsonElement args)
    {
        JsonElement properties = default;
        var hasProperties = schema.ValueKind == JsonValueKind.Object &&
                            schema.TryGetProperty("properties", out properties) &&
                            properties.ValueKind == JsonValueKind.Object;

        var required = Required(schema);
        var bound = new JsonObject();
        if (hasProperties)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (!Present(args, property.Name, out var value))
                {
                    if (required.Contains(property.Name))
                    {
                        throw new InvalidOperationException($"{property.Name}: required");
                    }

                    continue;
                }

                bound[property.Name] = Copy(property.Name, property.Value, value);
            }
        }

        foreach (var name in required)
        {
            if (hasProperties && properties.TryGetProperty(name, out _))
            {
                continue;
            }

            if (!Present(args, name, out _))
            {
                throw new InvalidOperationException($"{name}: required");
            }
        }

        return JsonSerializer.SerializeToElement(bound);
    }

    private static JsonNode Copy(string path, JsonElement schema, JsonElement value)
    {
        Check(path, schema, value);
        if (IsStructuredObject(schema))
        {
            return JsonNode.Parse(FilterObject(schema, value).GetRawText()) ?? new JsonObject();
        }

        return JsonNode.Parse(value.GetRawText())
            ?? throw new InvalidOperationException($"{path}: expected a JSON value");
    }

    private static void Check(string path, JsonElement schema, JsonElement value)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array && !MatchesEnum(choices, value))
        {
            throw new InvalidOperationException($"{path}: expected one of the listed values");
        }

        if (!schema.TryGetProperty("type", out var typeNode) || typeNode.ValueKind != JsonValueKind.String)
        {
            if (schema.TryGetProperty("properties", out var nested) &&
                nested.ValueKind == JsonValueKind.Object &&
                value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"{path}: expected object");
            }

            return;
        }

        switch (typeNode.GetString())
        {
            case "string":
                Expect(path, value.ValueKind == JsonValueKind.String, "string");
                return;
            case "integer":
                Expect(path, value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _), "integer");
                return;
            case "number":
                Expect(path, value.ValueKind == JsonValueKind.Number, "number");
                return;
            case "boolean":
                Expect(path, value.ValueKind is JsonValueKind.True or JsonValueKind.False, "boolean");
                return;
            case "array":
                Expect(path, value.ValueKind == JsonValueKind.Array, "array");
                if (schema.TryGetProperty("items", out var items))
                {
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        var itemPath = $"{path}.{index}";
                        Check(itemPath, items, item);
                        if (IsStructuredObject(items) && item.ValueKind == JsonValueKind.Object)
                        {
                            FilterObject(items, item);
                        }

                        index++;
                    }
                }

                return;
            case "object":
                Expect(path, value.ValueKind == JsonValueKind.Object, "object");
                return;
            default:
                return;
        }
    }

    private static bool IsStructuredObject(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        return type.GetString() == "object";
    }

    private static bool MatchesEnum(JsonElement choices, JsonElement value)
    {
        foreach (var choice in choices.EnumerateArray())
        {
            if (JsonNode.DeepEquals(JsonNode.Parse(choice.GetRawText()), JsonNode.Parse(value.GetRawText())))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> Required(JsonElement schema)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("required", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return required;
        }

        foreach (var item in items.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!string.IsNullOrEmpty(name))
            {
                required.Add(name!);
            }
        }

        return required;
    }

    private static bool Present(JsonElement args, string name, out JsonElement value)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(name, out value) &&
            value.ValueKind != JsonValueKind.Undefined &&
            value.ValueKind != JsonValueKind.Null)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static void Expect(string path, bool ok, string expected)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"{path}: expected {expected}");
        }
    }
}
