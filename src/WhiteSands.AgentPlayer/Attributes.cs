namespace WhiteSands.AgentPlayer;

/// <summary>Class-level description the model sees for this player.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class AgentPlayerAttribute : Attribute
{
    public string? Description { get; set; }
}

/// <summary>Description of one action. The method stays a tool either way; this text is what the model reads.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class AgentActionAttribute : Attribute
{
    public AgentActionAttribute(string description)
    {
        Description = description;
    }

    public string Description { get; }
}

/// <summary>Leave this public method off the tool list.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class AgentIgnoreAttribute : Attribute
{
}
