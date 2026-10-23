using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer;

/// <summary>The platform rejected the key.</summary>
public class PlatformAuthException : Exception
{
    public PlatformAuthException(string message) : base(message)
    {
    }
}

/// <summary>This SDK and the platform disagree on the protocol.</summary>
public class ProtocolVersionException : Exception
{
    public ProtocolVersionException(string message) : base(message)
    {
    }
}

/// <summary>The action and the retry both failed. <see cref="Attempts"/> lists both calls.</summary>
public class TurnFailedException : Exception
{
    public TurnFailedException(string message, IReadOnlyList<AttemptRecord>? attempts = null)
        : base(message)
    {
        Attempts = attempts ?? Array.Empty<AttemptRecord>();
    }

    public IReadOnlyList<AttemptRecord> Attempts { get; }
}

/// <summary>The platform did not finish within the turn timeout.</summary>
public class TurnTimeoutException : Exception
{
    public TurnTimeoutException(string message) : base(message)
    {
    }
}

/// <summary>The socket dropped and the session could not be resumed.</summary>
public class ConnectionLostException : Exception
{
    public ConnectionLostException(string message = "Connection to the platform was lost")
        : base(message)
    {
    }
}
