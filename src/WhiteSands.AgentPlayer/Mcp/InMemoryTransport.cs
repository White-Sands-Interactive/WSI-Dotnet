using System.Text.Json;

namespace WhiteSands.AgentPlayer.Mcp;

/// <summary>Linked pair used by the offline platform. No socket.</summary>
public sealed class InMemoryTransport : IMcpTransport
{
    private readonly object _gate = new();
    private readonly Queue<JsonElement> _pending = new();
    private InMemoryTransport? _peer;
    private Action<JsonElement>? _handler;
    private bool _started;
    private bool _closed;

    public static (InMemoryTransport Server, InMemoryTransport Client) CreateLinkedPair()
    {
        var server = new InMemoryTransport();
        var client = new InMemoryTransport();
        server._peer = client;
        client._peer = server;
        return (server, client);
    }

    public void OnMessage(Action<JsonElement> handler)
    {
        List<JsonElement> queued;
        lock (_gate)
        {
            _handler = handler;
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
        var peer = _peer ?? throw new InvalidOperationException("Transport is not linked");
        peer.Receive(message.Clone());
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return Task.CompletedTask;
            }

            _closed = true;
            _handler = null;
        }

        return Task.CompletedTask;
    }

    private void Receive(JsonElement message)
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
                _pending.Enqueue(message);
                return;
            }

            handler = _handler;
        }

        handler(message);
    }
}
