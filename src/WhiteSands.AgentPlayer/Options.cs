using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhiteSands.AgentPlayer.Mcp;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer;

public sealed class TurnResult
{
    public TurnResult(string action, JsonElement args, JsonElement result, int attempts, TokenUsage usage)
    {
        Action = action;
        Args = args;
        Result = result;
        Attempts = attempts;
        Usage = usage;
    }

    public string Action { get; }

    public JsonElement Args { get; }

    public JsonElement Result { get; }

    public int Attempts { get; }

    public TokenUsage Usage { get; }
}

public sealed class TurnOptions
{
    /// <summary>Overrides the server default. The turn throws <see cref="TurnTimeoutException"/> when exceeded.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Aborts the wait for the platform's turn result.</summary>
    public CancellationToken CancellationToken { get; set; }
}

/// <summary>One started player. <see cref="TurnAsync"/> grants that character one chance to act.</summary>
public interface IAgentHandle
{
    string SessionId { get; }

    Task<TurnResult> TurnAsync(TurnOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Stop the remote agent for this session. The <see cref="AgentServer"/> stays up.</summary>
    Task StopAsync();

    /// <summary>Subscribe to this session's action, retry, and failure events. Dispose to unsubscribe.</summary>
    IDisposable On(Action<AgentEvent> listener);
}

public sealed class StartOptions
{
    /// <summary>Label stored with the session. Defaults to the class name.</summary>
    public string? Name { get; set; }

    /// <summary>Added to the model prompt for this player only.</summary>
    public string? Goal { get; set; }

    /// <summary>Extra instructions appended to the default agent prompt.</summary>
    public string? Instructions { get; set; }
}

/// <summary>In-process platform used by <see cref="Testing.TestPlatform"/> and local runs.</summary>
public interface IInlinePlatform
{
    Task StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default);

    Task StopSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class StartSessionRequest
{
    public StartSessionRequest(
        string sessionId,
        string sessionToken,
        string? name,
        IReadOnlyList<ToolDescriptor> tools,
        string instructions,
        string? goal,
        ILogger? logger,
        IMcpTransport transport,
        Action<TurnReport> onTurnResult,
        Action<AgentEvent>? onEvent,
        Func<int, Task>? onUsage)
    {
        SessionId = sessionId;
        SessionToken = sessionToken;
        Name = name;
        Tools = tools;
        Instructions = instructions;
        Goal = goal;
        Logger = logger;
        Transport = transport;
        OnTurnResult = onTurnResult;
        OnEvent = onEvent;
        OnUsage = onUsage;
    }

    public string SessionId { get; }

    public string SessionToken { get; }

    public string? Name { get; }

    public IReadOnlyList<ToolDescriptor> Tools { get; }

    public string Instructions { get; }

    public string? Goal { get; }

    public ILogger? Logger { get; }

    public IMcpTransport Transport { get; }

    public Action<TurnReport> OnTurnResult { get; }

    public Action<AgentEvent>? OnEvent { get; }

    public Func<int, Task>? OnUsage { get; }
}

public sealed class AgentServerOptions
{
    /// <summary>
    /// Platform key naming the billing account.
    /// Defaults to the <c>WSI_PLATFORM_KEY</c> environment variable.
    /// Not required when <see cref="Inline"/> is set.
    /// </summary>
    public string? PlatformKey { get; set; }

    /// <summary>Test hook. Game code always uses <see cref="AgentServer.PlatformUrl"/>.</summary>
    internal string? PlatformUrl { get; set; }

    /// <summary>Skip the outbound WebSocket and run against this in-process platform.</summary>
    public IInlinePlatform? Inline { get; set; }

    /// <summary>Replaces the default agent instructions.</summary>
    public string? Instructions { get; set; }

    /// <summary>Default timeout for <c>TurnAsync</c>. Default: 60 seconds.</summary>
    public TimeSpan? TurnTimeout { get; set; }

    /// <summary>Receives connection and session events for every player on this server.</summary>
    public Action<AgentEvent>? OnEvent { get; set; }

    /// <summary>Where the SDK writes warnings. Nothing is written when omitted.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Action log included on later views as <c>done</c>.
    /// Defaults to an in-memory log. Pass a <see cref="Memory.IActionMemory"/> to persist it.
    /// </summary>
    public Memory.IActionMemory? Memory { get; set; }

    /// <summary>When true, views have no <c>done</c> field.</summary>
    public bool DisableMemory { get; set; }

    /// <summary>
    /// Runs player methods on this context. When null and <see cref="UseSynchronizationContext"/>
    /// is true, the context current at construction is used (Unity's main thread).
    /// </summary>
    public SynchronizationContext? InvokeOn { get; set; }

    /// <summary>Set by the caller when <see cref="InvokeOn"/> was assigned, including an explicit null.</summary>
    public bool InvokeOnSpecified { get; set; }

    /// <summary>When false, player methods run on the calling thread instead of a captured context.</summary>
    public bool UseSynchronizationContext { get; set; } = true;
}
