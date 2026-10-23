using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhiteSands.AgentPlayer.Mcp;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer;

internal sealed class ResumableSession
{
    public ResumableSession(string sessionId, string sessionToken)
    {
        SessionId = sessionId;
        SessionToken = sessionToken;
    }

    public string SessionId { get; }

    public string SessionToken { get; }
}

internal sealed class PlatformConnectionHandlers
{
    public Action<Envelope> OnEnvelope { get; set; } = _ => { };

    public Action? OnDisconnect { get; set; }

    public Action? OnReconnect { get; set; }

    public Action<Exception>? OnError { get; set; }

    public Func<IReadOnlyList<ResumableSession>>? Sessions { get; set; }
}

/// <summary>
/// Outbound WebSocket to the platform. The game never listens on a public URL.
/// After the first successful hello, a dropped socket reconnects and resumes sessions.
/// </summary>
internal sealed class PlatformConnection : IAsyncDisposable
{
    private const int HeartbeatMs = 15_000;
    private const int PongTimeoutMs = 10_000;
    private const int MaxReconnectAttempts = 8;

    private readonly string _url;
    private readonly string _platformKey;
    private readonly PlatformConnectionHandlers _handlers;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _state = new();
    private readonly Dictionary<string, Waiter> _pending = new();
    private readonly Queue<Envelope> _outbox = new();
    private readonly CancellationTokenSource _lifetime = new();

    private ClientWebSocket? _ws;
    private Timer? _heartbeat;
    private Timer? _pongTimer;
    private string? _pendingPong;
    private string? _accountName;
    private bool _closed;
    private bool _everConnected;
    private bool _reconnecting;
    private int _reconnectAttempt;

    public PlatformConnection(string url, string platformKey, PlatformConnectionHandlers handlers, ILogger logger)
    {
        _url = url;
        _platformKey = platformKey;
        _handlers = handlers;
        _logger = logger;
        ClientId = Guid.NewGuid().ToString();
    }

    public string ClientId { get; }

    public bool Connected
    {
        get
        {
            var ws = _ws;
            return ws != null && ws.State == WebSocketState.Open;
        }
    }

    public async Task<string> ConnectAsync()
    {
        if (_closed)
        {
            throw new InvalidOperationException("Platform connection is closed");
        }

        if (Connected && _accountName != null)
        {
            return _accountName;
        }

        var account = await OpenSocketAsync().ConfigureAwait(false);
        _everConnected = true;
        _reconnectAttempt = 0;
        return account;
    }

    public void Send(Envelope envelope)
    {
        var ws = _ws;
        if (ws != null && ws.State == WebSocketState.Open)
        {
            var json = ProtocolJson.Serialize(envelope);
            _ = TransmitAsync(ws, json);
            return;
        }

        if (envelope is McpEnvelope or PingEnvelope)
        {
            lock (_outbox)
            {
                _outbox.Enqueue(envelope);
            }

            return;
        }

        throw new InvalidOperationException("Not connected to platform");
    }

    public Task<Envelope> WaitForSessionEventAsync(string sessionId, string kind, int timeoutMs = 15_000)
    {
        var key = $"session:{sessionId}:{kind}";
        var source = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = new CancellationTokenSource(timeoutMs);
        var waiter = new Waiter(source, timeout);
        lock (_pending)
        {
            _pending[key] = waiter;
        }

        timeout.Token.Register(() =>
        {
            var removed = false;
            lock (_pending)
            {
                if (_pending.TryGetValue(key, out var current) && current == waiter)
                {
                    _pending.Remove(key);
                    removed = true;
                }
            }

            if (removed)
            {
                source.TrySetException(new TimeoutException($"Timed out waiting for session {kind}"));
            }
        });
        return source.Task;
    }

    public SessionMuxTransport CreateTransport(string sessionId) => new(sessionId, Send);

    public async Task CloseAsync()
    {
        _closed = true;
        StopHeartbeat();
        _reconnecting = false;
        _lifetime.Cancel();
        FailWaiters(new InvalidOperationException("platform connection closed"));
        var ws = _ws;
        _ws = null;
        if (ws == null)
        {
            return;
        }

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            ws.Abort();
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private async Task<string> OpenSocketAsync()
    {
        if (_closed)
        {
            throw new InvalidOperationException("Platform connection is closed");
        }

        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        var session = new SocketSession(ws);
        _ws = ws;
        try
        {
            await ws.ConnectAsync(new Uri(_url), _lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            if (_ws == ws)
            {
                _ws = null;
            }

            ws.Dispose();
            throw;
        }

        _ = PumpAsync(session);
        await TransmitAsync(ws, ProtocolJson.Serialize(new HelloEnvelope
        {
            PlatformKey = _platformKey,
            ClientId = ClientId,
            ProtocolVersion = ProtocolConstants.ProtocolVersion,
            SdkVersion = ProtocolConstants.SdkVersion,
        })).ConfigureAwait(false);
        return await session.Hello.Task.ConfigureAwait(false);
    }

    private async Task PumpAsync(SocketSession session)
    {
        var buffer = new byte[8192];
        try
        {
            while (!_closed && session.Socket.State == WebSocketState.Open)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await session.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        OnSocketClosed(session);
                        return;
                    }

                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(stream.ToArray());
                try
                {
                    HandleMessage(session, text);
                }
                catch (Exception ex)
                {
                    _handlers.OnError?.Invoke(ex);
                }
            }

            OnSocketClosed(session);
        }
        catch (Exception ex)
        {
            if (!_closed && ex is not OperationCanceledException)
            {
                _handlers.OnError?.Invoke(ex);
            }

            OnSocketClosed(session);
        }
    }

    private void HandleMessage(SocketSession session, string text)
    {
        Envelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(text, ProtocolJson.Options)
                ?? throw new InvalidOperationException("Invalid platform message");
        }
        catch (Exception ex)
        {
            _handlers.OnError?.Invoke(ex);
            return;
        }

        if (!session.Hello.Task.IsCompleted && envelope is HelloOkEnvelope or HelloErrEnvelope)
        {
            if (envelope is HelloErrEnvelope helloErr)
            {
                var error = helloErr.Code == "PROTOCOL"
                    ? (Exception)new ProtocolVersionException(helloErr.Message)
                    : new PlatformAuthException(helloErr.Message);
                _closed = true;
                session.Hello.TrySetException(error);
                session.Socket.Abort();
                return;
            }

            var ok = (HelloOkEnvelope)envelope;
            _accountName = ok.AccountName;
            _everConnected = true;
            StartHeartbeat();
            FlushOutbox();
            session.Hello.TrySetResult(ok.AccountName);
            return;
        }

        if (envelope is PongEnvelope pong)
        {
            if (_pendingPong == pong.Id)
            {
                _pendingPong = null;
                _pongTimer?.Dispose();
                _pongTimer = null;
            }

            return;
        }

        ResolveSessionWaiter(envelope);
        _handlers.OnEnvelope(envelope);
    }

    private void ResolveSessionWaiter(Envelope envelope)
    {
        switch (envelope)
        {
            case SessionStartedEnvelope started:
                CompleteWaiter($"session:{started.SessionId}:start", waiter => waiter.TrySetResult(envelope));
                return;
            case SessionStoppedEnvelope stopped:
                CompleteWaiter($"session:{stopped.SessionId}:stop", waiter => waiter.TrySetResult(envelope));
                return;
            case SessionResumedEnvelope:
            case ResumeErrEnvelope:
                var sessionId = envelope is SessionResumedEnvelope resumed
                    ? resumed.SessionId
                    : ((ResumeErrEnvelope)envelope).SessionId;
                CompleteWaiter($"session:{sessionId}:resume", waiter =>
                {
                    if (envelope is ResumeErrEnvelope resumeErr)
                    {
                        waiter.TrySetException(new ConnectionLostException(resumeErr.Message));
                        return;
                    }

                    waiter.TrySetResult(envelope);
                });
                return;
        }
    }

    private void CompleteWaiter(string key, Action<TaskCompletionSource<Envelope>> complete)
    {
        Waiter? waiter;
        lock (_pending)
        {
            if (!_pending.TryGetValue(key, out waiter))
            {
                return;
            }

            _pending.Remove(key);
        }

        waiter.Timeout.Dispose();
        complete(waiter.Source);
    }

    private void OnSocketClosed(SocketSession session)
    {
        if (Interlocked.Exchange(ref session.DidClose, 1) == 1)
        {
            return;
        }

        StopHeartbeat();
        if (_ws == session.Socket)
        {
            _ws = null;
        }

        if (!session.Hello.Task.IsCompleted)
        {
            session.Hello.TrySetException(new InvalidOperationException("platform connection closed before hello"));
        }

        if (_closed || !_everConnected)
        {
            FailWaiters(new InvalidOperationException("platform connection closed"));
            return;
        }

        _handlers.OnDisconnect?.Invoke();
        ScheduleReconnect();
    }

    private void ScheduleReconnect()
    {
        lock (_state)
        {
            if (_closed || _reconnecting)
            {
                return;
            }

            if (_reconnectAttempt >= MaxReconnectAttempts)
            {
                _closed = true;
                var error = new ConnectionLostException("Could not reconnect to the platform");
                _handlers.OnError?.Invoke(error);
                FailWaiters(error);
                return;
            }

            var delay = (int)Math.Min(
                ProtocolConstants.SessionResumeGrace.TotalMilliseconds,
                250 * Math.Pow(2, _reconnectAttempt));
            _reconnectAttempt += 1;
            _reconnecting = true;
            _logger.LogWarning($"platform connection dropped; retrying in {delay}ms");
            _ = ReconnectAfterAsync(delay);
        }
    }

    private async Task ReconnectAfterAsync(int delay)
    {
        try
        {
            await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ReconnectAsync().ConfigureAwait(false);
    }

    private async Task ReconnectAsync()
    {
        try
        {
            await OpenSocketAsync().ConfigureAwait(false);
            _reconnectAttempt = 0;
            _reconnecting = false;
            var sessions = _handlers.Sessions?.Invoke() ?? Array.Empty<ResumableSession>();
            foreach (var session in sessions)
            {
                var resumed = WaitForSessionEventAsync(session.SessionId, "resume");
                Send(new ResumeSessionEnvelope
                {
                    SessionId = session.SessionId,
                    SessionToken = session.SessionToken,
                });
                await resumed.ConfigureAwait(false);
            }

            _handlers.OnReconnect?.Invoke();
        }
        catch (Exception ex)
        {
            _reconnecting = false;
            if (_closed)
            {
                return;
            }

            if (ex is PlatformAuthException or ProtocolVersionException)
            {
                _closed = true;
                _handlers.OnError?.Invoke(ex);
                FailWaiters(ex);
                return;
            }

            ScheduleReconnect();
        }
    }

    private void FlushOutbox()
    {
        List<Envelope> pending;
        lock (_outbox)
        {
            pending = _outbox.ToList();
            _outbox.Clear();
        }

        foreach (var envelope in pending)
        {
            Send(envelope);
        }
    }

    private void StartHeartbeat()
    {
        StopHeartbeat();
        _heartbeat = new Timer(_ =>
        {
            if (!Connected)
            {
                return;
            }

            var id = Guid.NewGuid().ToString();
            _pendingPong = id;
            try
            {
                Send(new PingEnvelope { Id = id });
            }
            catch (Exception)
            {
                return;
            }

            _pongTimer?.Dispose();
            _pongTimer = new Timer(__ =>
            {
                if (_pendingPong == id)
                {
                    _logger.LogWarning("platform heartbeat timed out");
                    try
                    {
                        _ws?.Abort();
                    }
                    catch (Exception)
                    {
                        // already closed
                    }
                }
            }, null, PongTimeoutMs, Timeout.Infinite);
        }, null, HeartbeatMs, HeartbeatMs);
    }

    private void StopHeartbeat()
    {
        _heartbeat?.Dispose();
        _pongTimer?.Dispose();
        _heartbeat = null;
        _pongTimer = null;
        _pendingPong = null;
    }

    private async Task TransmitAsync(ClientWebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ws.State != WebSocketState.Open)
            {
                return;
            }

            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void FailWaiters(Exception error)
    {
        List<Waiter> waiters;
        lock (_pending)
        {
            waiters = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.Timeout.Dispose();
            waiter.Source.TrySetException(error);
        }
    }

    private sealed class SocketSession
    {
        public SocketSession(ClientWebSocket socket)
        {
            Socket = socket;
        }

        public ClientWebSocket Socket { get; }

        public TaskCompletionSource<string> Hello { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DidClose;
    }

    private sealed class Waiter
    {
        public Waiter(TaskCompletionSource<Envelope> source, CancellationTokenSource timeout)
        {
            Source = source;
            Timeout = timeout;
        }

        public TaskCompletionSource<Envelope> Source { get; }

        public CancellationTokenSource Timeout { get; }
    }
}
