using System.Text.Json;

namespace WhiteSands.AgentPlayer;

/// <summary>
/// Registers tools whose names and schemas come from data, rather than from methods on the player.
/// Bindings are read when <see cref="AgentServer.StartAsync"/> reflects the player.
/// </summary>
public static class PlayerDynamicTools
{
    /// <summary>
    /// Register one tool on <paramref name="player"/>.
    /// <c>see</c> replaces the reflected see tool. <c>wait</c>, an empty name, a name starting with <c>_</c>,
    /// a missing callback, and a schema that is not a JSON object are rejected.
    /// An empty description becomes <c>Action: {name}</c>. The same name replaces the previous tool.
    /// </summary>
    public static void BindDynamic(
        this object player,
        string name,
        string? description,
        JsonElement schema,
        Func<JsonElement, object?> callback)
    {
        Reflection.DynamicToolRegistry.Bind(player, name, description, schema, callback);
    }
}
