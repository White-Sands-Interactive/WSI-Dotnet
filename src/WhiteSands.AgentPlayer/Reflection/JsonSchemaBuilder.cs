using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WhiteSands.AgentPlayer.Reflection;

internal static class JsonSchemaBuilder
{
    public static JsonElement ForMethod(MethodInfo method)
    {
        var parameters = method.GetParameters();
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var parameter in parameters)
        {
            if (parameter.Name == null)
            {
                throw new InvalidOperationException($"Method {method.Name} has an unnamed parameter.");
            }

            if (parameter.ParameterType.IsByRef)
            {
                throw new InvalidOperationException($"Method {method.Name} parameter {parameter.Name} cannot be ref, out, or in.");
            }

            properties[parameter.Name] = SchemaFor(parameter.ParameterType, new HashSet<Type>());
            if (!IsOptional(parameter))
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return JsonSerializer.SerializeToElement(schema);
    }

    private static bool IsOptional(ParameterInfo parameter)
    {
        return parameter.HasDefaultValue || Nullable.GetUnderlyingType(parameter.ParameterType) != null;
    }

    private static JsonNode SchemaFor(Type type, HashSet<Type> seen)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            type = underlying;
        }

        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }

        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
        {
            return new JsonObject { ["type"] = "integer" };
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return new JsonObject { ["type"] = "number" };
        }

        if (type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonNode))
        {
            return new JsonObject();
        }

        if (type.IsEnum)
        {
            var values = new JsonArray();
            foreach (var value in Enum.GetValues(type))
            {
                values.Add(Convert.ToInt64(value));
            }

            return new JsonObject { ["type"] = "integer", ["enum"] = values };
        }

        if (type.IsArray)
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = SchemaFor(type.GetElementType()!, seen),
            };
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) || definition == typeof(IList<>) ||
                definition == typeof(IReadOnlyList<>) || definition == typeof(IEnumerable<>))
            {
                return new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = SchemaFor(type.GetGenericArguments()[0], seen),
                };
            }

            if (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) ||
                definition == typeof(IReadOnlyDictionary<,>))
            {
                return new JsonObject { ["type"] = "object" };
            }
        }

        if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
        {
            if (!seen.Add(type))
            {
                return new JsonObject { ["type"] = "object" };
            }

            var properties = new JsonObject();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                properties[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] =
                    SchemaFor(property.PropertyType, seen);
            }

            seen.Remove(type);
            return new JsonObject { ["type"] = "object", ["properties"] = properties };
        }

        return new JsonObject();
    }
}
