using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhiteSands.AgentPlayer.Mcp;
using WhiteSands.AgentPlayer.Memory;
using WhiteSands.AgentPlayer.Protocol;
using WhiteSands.AgentPlayer.Reflection;

namespace WhiteSands.AgentPlayer;

/// <summary>
/// One in-process MCP server and one outbound connection to the platform.
/// Every <see cref="StartAsync"/> is its own session. The game calls <c>TurnAsync</c> to
/// decide whose turn it is; the platform chooses the action.
/// </summary>
public sealed class AgentServer : IAsyncDisposable
{
    public const string DefaultPlatformUrl = "ws://127.0.0.1:8787";
    private static readonly TimeSpan DefaultTurnTimeout = TimeSpan.FromSeconds(60);

    private readonly AgentServerOptions _options;
    private readonly string _platformKey;
    private readonly string _platformUrl;
    private readonly ILogger _logger;
    private readonly IInlinePlatform? _inline;
    private readonly SynchronizationContext? _context;
    private readonly Dictionary<string, SessionRecord> _sessions = new();
    private readonly List<Action<AgentEvent>> _listeners = new();
    private readonly object _listenerGate = new();
    private PlatformConnection? _connection;
    private IActionMemory? _memory;
    private bool _started;
    private bool _closing;

    public AgentServer(AgentServerOptions? options = null)
    {
        options ??= new AgentServerOptions();
        var platformKey = options.PlatformKey ?? Environment.GetEnvironmentVariable("WSI_PLATFORM_KEY");
        if (options.Inline == null && string.IsNullOrEmpty(platformKey))
        {
            throw new InvalidOperationException(
                "Set WSI_PLATFORM_KEY or pass platformKey, unless an inline platform is provided");
        }

        _options = options;
        _platformKey = string.IsNullOrEmpty(platformKey) ? "inline" : platformKey!;
        _platformUrl = options.PlatformUrl
            ?? Environment.GetEnvironmentVariable("WSI_PLATFORM_URL")
            ?? DefaultPlatformUrl;
        _logger = options.Logger ?? NullLogger.Instance;
        _inline = options.Inline;
        if (options.OnEvent != null)
        {
            _listeners.Add(options.OnEvent);
        }

        if (options.InvokeOnSpecified)
        {
            _context = options.InvokeOn;
        }
        else if (options.UseSynchronizationContext)
        {
            _context = SynchronizationContext.Current;
        }
    }

    /// <summary>Ensure the outbound platform connection. No-op for an inline platform.</summary>
    public async Task ConnectAsync()
    {
        if (_inline != null || _started)
        {
            _started = true;
            return;
        }

        _connection = new PlatformConnection(_platformUrl, _platformKey, new PlatformConnectionHandlers
        {
            OnEnvelope = HandleEnvelope,
            OnDisconnect = () => Emit(new DisconnectedEvent()),
            OnReconnect = () => Emit(new ReconnectedEvent()),
            OnError = error =>
            {
                _logger.LogError(error.Message);
                if (error is ConnectionLostException lost)
                {
                    FailInFlight(lost);
                }
            },
            Sessions = () => _sessions.Values
                .Select(session => new ResumableSession(session.Id, session.Token))
                .ToArray(),
        }, _logger);
        await _connection.ConnectAsync().ConfigureAwait(false);
        _started = true;
    }

    /// <summary>
    /// Reflect the player into tools, register a session, and return a handle.
    /// <c>TurnAsync</c> on that handle lets the remote model act once.
    /// </summary>
    public async Task<IAgentHandle> StartAsync(object player, StartOptions? options = null)
    {
        await ConnectAsync().ConfigureAwait(false);
        var reflected = PlayerReflector.Reflect(player, CreateInvoker());
        var sessionId = Guid.NewGuid().ToString();
        var sessionToken = Guid.NewGuid().ToString();
        var gate = new TurnGate();
        var name = options?.Name ?? player.GetType().Name;
        var memory = ActionMemoryStore();
        var server = new SessionMcpServer(
            reflected,
            gate,
            $"agent-player:{name}",
            memory == null ? null : new SessionActionLog(memory, sessionId, name));
        var instructions = ComposeInstructions(options);
        var listeners = new List<Action<AgentEvent>>();
        var record = new SessionRecord(sessionId, sessionToken, name, gate, server, listeners);

        void OnTurnResult(TurnReport result) => gate.CompleteTurn(result);

        if (_inline != null)
        {
            var (serverTransport, clientTransport) = InMemoryTransport.CreateLinkedPair();
            server.Attach(serverTransport);
            await serverTransport.StartAsync().ConfigureAwait(false);
            record.Transport = serverTransport;
            _sessions[sessionId] = record;
            await _inline.StartSessionAsync(new StartSessionRequest(
                sessionId,
                sessionToken,
                name,
                reflected.Tools,
                instructions,
                options?.Goal,
                _options.Logger,
                clientTransport,
                OnTurnResult,
                Emit,
                null)).ConfigureAwait(false);
        }
        else
        {
            if (_connection == null)
            {
                throw new InvalidOperationException("Not connected");
            }

            var mux = _connection.CreateTransport(sessionId);
            server.Attach(mux);
            await mux.StartAsync().ConfigureAwait(false);
            record.Transport = mux;
            record.Mux = mux;
            _sessions[sessionId] = record;
            var started = _connection.WaitForSessionEventAsync(sessionId, "start");
            _connection.Send(new StartSessionEnvelope
            {
                SessionId = sessionId,
                SessionToken = sessionToken,
                Name = name,
                Tools = reflected.Tools.ToList(),
                Instructions = instructions,
                Goal = options?.Goal,
            });
            await started.ConfigureAwait(false);
        }

        return new Handle(sessionId, this, listeners);
    }

    /// <summary>Subscribe to every session plus connection events. Dispose to unsubscribe.</summary>
    public IDisposable On(Action<AgentEvent> listener)
    {
        lock (_listenerGate)
        {
            _listeners.Add(listener);
        }

        return new Subscription(() =>
        {
            lock (_listenerGate)
            {
                _listeners.Remove(listener);
            }
        });
    }

    /// <summary>Stop every session and close the platform connection.</summary>
    public async Task CloseAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        foreach (var id in _sessions.Keys.ToList())
        {
            await StopSessionAsync(id).ConfigureAwait(false);
        }

        if (_inline != null)
        {
            await _inline.CloseAsync().ConfigureAwait(false);
        }

        if (_connection != null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            _connection = null;
        }

        _memory?.Dispose();
        _memory = null;
        _started = false;
        _closing = false;
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task<TurnResult> TurnAsync(string sessionId, TurnOptions? options, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"Unknown session {sessionId}");
        }

        var timeout = options?.Timeout ?? _options.TurnTimeout ?? DefaultTurnTimeout;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            options?.CancellationToken ?? CancellationToken.None);
        var outcome = await session.Gate.RunTurnAsync(timeout, linked.Token).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            if (outcome.ConnectionLost)
            {
                throw new ConnectionLostException(outcome.Error ?? "Connection to the platform was lost");
            }

            throw new TurnFailedException(outcome.Error ?? "turn failed", outcome.AttemptDetails);
        }

        return new TurnResult(
            outcome.Action ?? "unknown",
            outcome.Args ?? ProtocolJson.EmptyObject(),
            outcome.Result ?? ProtocolJson.Parse("null"),
            outcome.Attempts ?? 1,
            outcome.Usage ?? new TokenUsage(0, 0));
    }

    private void Emit(AgentEvent evt)
    {
        Action<AgentEvent>[] listeners;
        lock (_listenerGate)
        {
            listeners = _listeners.ToArray();
        }

        foreach (var listener in listeners)
        {
            listener(evt);
        }

        if (evt is DisconnectedEvent or ReconnectedEvent)
        {
            return;
        }

        var sessionId = evt switch
        {
            ActionEvent action => action.SessionId,
            RetryEvent retry => retry.SessionId,
            TurnFailedEvent failed => failed.SessionId,
            _ => null,
        };
        if (sessionId == null || !_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        Action<AgentEvent>[] sessionListeners;
        lock (session.Listeners)
        {
            sessionListeners = session.Listeners.ToArray();
        }

        foreach (var listener in sessionListeners)
        {
            listener(evt);
        }
    }

    private void FailInFlight(ConnectionLostException error)
    {
        foreach (var session in _sessions.Values.ToArray())
        {
            session.Gate.FailTurn(error);
        }
    }

    private void HandleEnvelope(Envelope envelope)
    {
        switch (envelope)
        {
            case McpEnvelope mcp:
                if (_sessions.TryGetValue(mcp.SessionId, out var mcpSession))
                {
                    mcpSession.Mux?.Receive(mcp.Message);
                }

                return;
            case TurnResultEnvelope turn:
                if (_sessions.TryGetValue(turn.SessionId, out var turnSession))
                {
                    turnSession.Gate.CompleteTurn(turn.ToReport());
                }

                return;
            case SessionEventEnvelope sessionEvent:
                if (sessionEvent.Event == "action")
                {
                    Emit(new ActionEvent(
                        sessionEvent.SessionId,
                        sessionEvent.Action ?? "unknown",
                        sessionEvent.Args ?? ProtocolJson.EmptyObject()));
                    return;
                }

                if (sessionEvent.Event == "retry")
                {
                    Emit(new RetryEvent(
                        sessionEvent.SessionId,
                        sessionEvent.Action ?? "unknown",
                        sessionEvent.Args ?? ProtocolJson.EmptyObject(),
                        sessionEvent.Error ?? "action failed"));
                    return;
                }

                Emit(new TurnFailedEvent(
                    sessionEvent.SessionId,
                    sessionEvent.Error ?? "turn failed",
                    sessionEvent.Action,
                    sessionEvent.Args));
                return;
            case ErrorEnvelope error:
                _logger.LogError(error.Message);
                return;
            case SessionStartedEnvelope:
            case SessionStoppedEnvelope:
            case SessionResumedEnvelope:
            case ResumeErrEnvelope:
            case HelloOkEnvelope:
            case HelloErrEnvelope:
            case HelloEnvelope:
            case StartSessionEnvelope:
            case StopSessionEnvelope:
            case PingEnvelope:
            case PongEnvelope:
            case ResumeSessionEnvelope:
                return;
            default:
                throw new InvalidOperationException($"Unhandled protocol message: {JsonSerializer.Serialize(envelope, ProtocolJson.Options)}");
        }
    }

    private async Task StopSessionAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        _sessions.Remove(sessionId);
        session.Gate.Reset();

        if (_inline != null)
        {
            await _inline.StopSessionAsync(sessionId).ConfigureAwait(false);
        }
        else if (_connection != null)
        {
            var stopped = _connection.WaitForSessionEventAsync(sessionId, "stop");
            try
            {
                _connection.Send(new StopSessionEnvelope { SessionId = sessionId });
            }
            catch (Exception)
            {
                // connection may already be down
            }

            try
            {
                await stopped.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // already gone
            }
        }

        try
        {
            await session.Server.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ignore
        }
    }

    private string ComposeInstructions(StartOptions? options)
    {
        var parts = new List<string>();
        var primary = _options.Instructions ?? ProtocolConstants.DefaultAgentInstructions;
        if (!string.IsNullOrEmpty(primary))
        {
            parts.Add(primary);
        }

        if (!string.IsNullOrEmpty(options?.Instructions))
        {
            parts.Add(options!.Instructions!);
        }

        return string.Join("\n\n", parts);
    }

    private IActionMemory? ActionMemoryStore()
    {
        if (_options.DisableMemory)
        {
            return null;
        }

        _memory ??= _options.Memory ?? new InMemoryActionMemory();
        return _memory;
    }

    private PlayerInvoker? CreateInvoker()
    {
        var context = _context;
        if (context == null)
        {
            return null;
        }

        return call =>
        {
            var source = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Post(_ =>
            {
                call().ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        source.TrySetException(task.Exception!.GetBaseException());
                    }
                    else if (task.IsCanceled)
                    {
                        source.TrySetCanceled();
                    }
                    else
                    {
                        source.TrySetResult(task.Result);
                    }
                }, TaskScheduler.Default);
            }, null);
            return source.Task;
        };
    }

    private sealed class SessionRecord
    {
        public SessionRecord(
            string id,
            string token,
            string name,
            TurnGate gate,
            SessionMcpServer server,
            List<Action<AgentEvent>> listeners)
        {
            Id = id;
            Token = token;
            Name = name;
            Gate = gate;
            Server = server;
            Listeners = listeners;
        }

        public string Id { get; }

        public string Token { get; }

        public string Name { get; }

        public TurnGate Gate { get; }

        public SessionMcpServer Server { get; }

        public List<Action<AgentEvent>> Listeners { get; }

        public IMcpTransport? Transport { get; set; }

        public SessionMuxTransport? Mux { get; set; }
    }

    private sealed class Handle : IAgentHandle
    {
        private readonly AgentServer _server;
        private readonly List<Action<AgentEvent>> _listeners;

        public Handle(string sessionId, AgentServer server, List<Action<AgentEvent>> listeners)
        {
            SessionId = sessionId;
            _server = server;
            _listeners = listeners;
        }

        public string SessionId { get; }

        public Task<TurnResult> TurnAsync(TurnOptions? options = null, CancellationToken cancellationToken = default) =>
            _server.TurnAsync(SessionId, options, cancellationToken);

        public Task StopAsync() => _server.StopSessionAsync(SessionId);

        public IDisposable On(Action<AgentEvent> listener)
        {
            lock (_listeners)
            {
                _listeners.Add(listener);
            }

            return new Subscription(() =>
            {
                lock (_listeners)
                {
                    _listeners.Remove(listener);
                }
            });
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _unsubscribe;

        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}
