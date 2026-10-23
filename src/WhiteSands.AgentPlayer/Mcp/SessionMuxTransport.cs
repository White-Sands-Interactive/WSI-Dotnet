using System.Text.Json;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Mcp;

/// <summary>
/// MCP transport that multiplexes JSON-RPC over a shared envelope channel, keyed by session id.
/// </summary>
public sealed class SessionMuxTransport : IMcpTransport
{
    private readonly object _gate = new();
    private readonly Queue<JsonElement> _pending = new();
    private readonly string _sessionId;
    private readonly Action<Envelope> _sendEnvelope;
    private Action<JsonElement>? _handler;
    private bool _started;
    private bool _closed;

    public SessionMuxTransport(string sessionId, Action<Envelope> sendEnvelope)
    {
        _sessionId = sessionId;
        _sendEnvelope = sendEnvelope;
    }

    public string SessionId => _sessionId;

    public void OnMessage(Action<JsonElement> handler)
    {
        List<JsonElement> queued;
        lock (_gate)
        {
            _handler = handler;
            if (!_started)
            {
                return;
            }

            queued = _pending.ToList();
            _pending.Clear();
        }

        foreach (var message in queued)
        {
            handler(message);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        List<JsonElement> queued;
        Action<JsonElement>? handler;
        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException("Transport already closed");
            }

            _started = true;
            handler = _handler;
            queued = _pending.ToList();
            _pending.Clear();
        }

        if (handler != null)
        {
            foreach (var message in queued)
            {
                handler(message);
            }
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(JsonElement message, CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            throw new InvalidOperationException("Transport closed");
        }

        _sendEnvelope(new McpEnvelope { SessionId = _sessionId, Message = message.Clone() });
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        _closed = true;
        return Task.CompletedTask;
    }

    public void Receive(JsonElement message)
    {
        Action<JsonElement>? handler;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (!_started || _handler == null)
            {
                _pending.Enqueue(message.Clone());
                return;
            }

            handler = _handler;
        }

        try
        {
            handler(message);
        }
        catch (Exception)
        {
            // The server handler reports its own failures as JSON-RPC errors.
        }
    }
}
