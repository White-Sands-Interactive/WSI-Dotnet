using System.Reflection;
using System.Text.Json;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Reflection;

/// <summary>
/// Publish tools from a player instance.
/// <c>See</c> is required. Every other public method is an action.
/// Names starting with <c>_</c> and methods marked <see cref="AgentIgnoreAttribute"/> are skipped.
/// </summary>
public static class PlayerReflector
{
    public static ReflectedPlayer Reflect(object player, PlayerInvoker? invoker = null)
    {
        if (player == null)
        {
            throw new ArgumentNullException(nameof(player));
        }

        var methods = CollectMethods(player.GetType());
        if (!methods.TryGetValue("see", out var seeMethod))
        {
            throw new InvalidOperationException("Player must implement a public see() method");
        }

        RejectHiddenDeclarations(player.GetType(), methods);

        var description = player.GetType().GetCustomAttribute<AgentPlayerAttribute>(inherit: true)?.Description;
        var see = BuildTool(player, seeMethod, "see", ProtocolConstants.SeeDescription, invoker);
        var actions = new List<PlayerTool>();
        foreach (var pair in methods.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (pair.Key == "see")
            {
                continue;
            }

            var actionDescription = pair.Value.GetCustomAttribute<AgentActionAttribute>()?.Description
                ?? $"Action: {pair.Key}";
            actions.Add(BuildTool(player, pair.Value, pair.Key, actionDescription, invoker));
        }

        var waitSchema = ProtocolJson.Parse("""{"type":"object","properties":{}}""");
        var wait = new ToolDescriptor("wait", ProtocolConstants.WaitDescription, waitSchema);
        var tools = new List<ToolDescriptor> { see.Descriptor };
        tools.AddRange(actions.Select(action => action.Descriptor));
        tools.Add(wait);

        return new ReflectedPlayer(description, see, actions, tools);
    }

    private static Dictionary<string, MethodInfo> CollectMethods(Type type)
    {
        var methods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            foreach (var method in current.GetMethods(flags))
            {
                if (!IsToolMethod(method))
                {
                    continue;
                }

                var name = JsonNamingPolicy.CamelCase.ConvertName(method.Name);
                if (name.Length == 0 || name.StartsWith("_", StringComparison.Ordinal) || name == "wait")
                {
                    continue;
                }

                if (!methods.ContainsKey(name))
                {
                    methods.Add(name, method);
                }
            }
        }

        return methods;
    }

    private static bool IsToolMethod(MethodInfo method)
    {
        if (method.IsSpecialName || method.IsGenericMethod || method.IsStatic)
        {
            return false;
        }

        if (method.Name.StartsWith("_", StringComparison.Ordinal))
        {
            return false;
        }

        return method.GetCustomAttribute<AgentIgnoreAttribute>() == null;
    }

    private static void RejectHiddenDeclarations(Type type, Dictionary<string, MethodInfo> published)
    {
        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var method in current.GetMethods(flags))
            {
                if (method.GetCustomAttribute<AgentActionAttribute>() == null)
                {
                    continue;
                }

                var name = JsonNamingPolicy.CamelCase.ConvertName(method.Name);
                if (!published.ContainsKey(name))
                {
                    throw new InvalidOperationException(
                        $"Declared action \"{name}\" is not a public method on {type.Name}");
                }
            }
        }
    }

    private static PlayerTool BuildTool(
        object player,
        MethodInfo method,
        string name,
        string description,
        PlayerInvoker? invoker)
    {
        var schema = JsonSchemaBuilder.ForMethod(method);
        var descriptor = new ToolDescriptor(name, description, schema);
        var names = method.GetParameters().Select(parameter => parameter.Name ?? "").ToArray();

        async Task<object?> Invoke(JsonElement args)
        {
            async Task<object?> Call()
            {
                var values = Bind(method, args);
                object? raw;
                try
                {
                    raw = method.Invoke(player, values);
                }
                catch (TargetInvocationException ex)
                {
                    throw ex.InnerException ?? ex;
                }

                return await Unwrap(raw).ConfigureAwait(false);
            }

            if (invoker == null)
            {
                return await Call().ConfigureAwait(false);
            }

            return await invoker(Call).ConfigureAwait(false);
        }

        return new PlayerTool(name, description, names, descriptor, Invoke);
    }

    private static object?[] Bind(MethodInfo method, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            args = ProtocolJson.EmptyObject();
        }

        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            values[i] = BindOne(parameters[i], args);
        }

        return values;
    }

    private static object? BindOne(ParameterInfo parameter, JsonElement args)
    {
        var name = parameter.Name ?? "arguments";
        var hasValue = args.TryGetProperty(name, out var value) &&
                       value.ValueKind != JsonValueKind.Undefined &&
                       value.ValueKind != JsonValueKind.Null;
        if (!hasValue)
        {
            if (parameter.HasDefaultValue)
            {
                return parameter.DefaultValue;
            }

            if (Nullable.GetUnderlyingType(parameter.ParameterType) != null)
            {
                return null;
            }

            throw new InvalidOperationException($"{name}: required");
        }

        try
        {
            return value.Deserialize(parameter.ParameterType, ProtocolJson.Body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{name}: {ex.Message}", ex);
        }
    }

    private static async Task<object?> Unwrap(object? result)
    {
        switch (result)
        {
            case null:
                return null;
            case Task task:
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                if (!taskType.IsGenericType)
                {
                    return null;
                }

                return taskType.GetProperty("Result")?.GetValue(task);
        }

        var resultType = result.GetType();
        if (resultType == typeof(ValueTask))
        {
            await ((ValueTask)result).ConfigureAwait(false);
            return null;
        }

        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = (Task?)resultType.GetMethod("AsTask")?.Invoke(result, null)
                ?? throw new InvalidOperationException("ValueTask.AsTask failed.");
            await asTask.ConfigureAwait(false);
            return resultType.GetProperty("Result")?.GetValue(result);
        }

        return result;
    }
}
