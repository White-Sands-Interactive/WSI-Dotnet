using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WhiteSands.AgentPlayer.Reflection;

internal sealed class DynamicToolBinding
{
    public DynamicToolBinding(string name, string description, JsonElement schema, Func<JsonElement, object?> callback)
    {
        Name = name;
        Description = description;
        Schema = schema;
        Callback = callback;
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement Schema { get; }

    public Func<JsonElement, object?> Callback { get; }
}

internal static class DynamicToolRegistry
{
    private static readonly ConditionalWeakTable<object, List<DynamicToolBinding>> Bindings = new();

    public static void Bind(
        object player,
        string name,
        string? description,
        JsonElement schema,
        Func<JsonElement, object?> callback)
    {
        if (player == null)
        {
            throw new ArgumentNullException(nameof(player));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Tool name is required");
        }

        if (name.StartsWith("_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Tool name cannot start with '_'");
        }

        if (name == "wait")
        {
            throw new InvalidOperationException("Tool name \"wait\" is reserved");
        }

        if (callback == null)
        {
            throw new InvalidOperationException("Tool callback is required");
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Tool schema must be a JSON object");
        }

        var text = string.IsNullOrEmpty(description) ? $"Action: {name}" : description;
        var binding = new DynamicToolBinding(name, text, schema.Clone(), callback);
        var list = Bindings.GetOrCreateValue(player);
        lock (list)
        {
            var index = list.FindIndex(item => item.Name == name);
            if (index >= 0)
            {
                list[index] = binding;
            }
            else
            {
                list.Add(binding);
            }
        }
    }

    public static IReadOnlyList<DynamicToolBinding> List(object player)
    {
        if (!Bindings.TryGetValue(player, out var list))
        {
            return Array.Empty<DynamicToolBinding>();
        }

        lock (list)
        {
            return list.ToArray();
        }
    }
}
