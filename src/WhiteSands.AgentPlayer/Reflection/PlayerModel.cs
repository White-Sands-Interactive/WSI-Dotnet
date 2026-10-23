using System.Text.Json;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Reflection;

/// <summary>Runs a player call, usually posted onto a captured <see cref="SynchronizationContext"/>.</summary>
public delegate Task<object?> PlayerInvoker(Func<Task<object?>> call);

public sealed class PlayerTool
{
    public PlayerTool(
        string name,
        string description,
        IReadOnlyList<string> paramNames,
        ToolDescriptor descriptor,
        Func<JsonElement, Task<object?>> invoke)
    {
        Name = name;
        Description = description;
        ParamNames = paramNames;
        Descriptor = descriptor;
        InvokeAsync = invoke;
    }

    public string Name { get; }

    public string Description { get; }

    public IReadOnlyList<string> ParamNames { get; }

    public ToolDescriptor Descriptor { get; }

    public Func<JsonElement, Task<object?>> InvokeAsync { get; }
}

public sealed class ReflectedPlayer
{
    public ReflectedPlayer(
        string? description,
        PlayerTool see,
        IReadOnlyList<PlayerTool> actions,
        IReadOnlyList<ToolDescriptor> tools)
    {
        Description = description;
        See = see;
        Actions = actions;
        Tools = tools;
    }

    public string? Description { get; }

    public PlayerTool See { get; }

    public IReadOnlyList<PlayerTool> Actions { get; }

    public IReadOnlyList<ToolDescriptor> Tools { get; }
}
