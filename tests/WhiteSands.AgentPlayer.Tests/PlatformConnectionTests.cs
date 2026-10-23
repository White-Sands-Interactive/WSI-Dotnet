using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Tests;

public class PlatformConnectionTests
{
    private FakePlatform? _platform;

    [Fact]
    public async Task SendsTheProtocolVersionAndMapsHelloErrCodesToTypedErrors()
    {
        _platform = await FakePlatform.StartAsync("PROTOCOL", "upgrade");
        var outdated = new AgentServer(new AgentServerOptions
        {
            PlatformKey = "pk_test",
            PlatformUrl = _platform.Url,
        });
        try
        {
            var error = await Assert.ThrowsAsync<ProtocolVersionException>(() => outdated.StartAsync(new Pawn()));
            Assert.Equal("upgrade", error.Message);
            Assert.Equal("hello", _platform.Received[0].GetProperty("type").GetString());
            Assert.Equal(ProtocolConstants.ProtocolVersion, _platform.Received[0].GetProperty("protocolVersion").GetInt32());
        }
        finally
        {
            await outdated.CloseAsync();
            await _platform.DisposeAsync();
        }

        _platform = await FakePlatform.StartAsync("AUTH", "bad key");
        var unauthorized = new AgentServer(new AgentServerOptions
        {
            PlatformKey = "pk_wrong",
            PlatformUrl = _platform.Url,
        });
        try
        {
            var error = await Assert.ThrowsAsync<PlatformAuthException>(() => unauthorized.StartAsync(new Pawn()));
            Assert.Equal("bad key", error.Message);
        }
        finally
        {
            await unauthorized.CloseAsync();
            await _platform.DisposeAsync();
            _platform = null;
        }
    }

    [Fact]
    public async Task ReconnectsResumesAndFinishesTheTurnOnTheNewSocket()
    {
        _platform = await FakePlatform.StartAsync();
        var events = new List<string>();
        var agents = new AgentServer(new AgentServerOptions
        {
            PlatformKey = "pk_test",
            PlatformUrl = _platform.Url,
            OnEvent = evt => events.Add(evt.Type),
        });
        try
        {
            var agent = await agents.StartAsync(new Pawn(), new StartOptions { Goal = "Step" });
            var started = _platform.Received.Single(envelope => envelope.GetProperty("type").GetString() == "start_session");
            Assert.Equal("Step", started.GetProperty("goal").GetString());
            Assert.True(started.GetProperty("tools").GetArrayLength() >= 3);

            _platform.AbortFirstSocket();
            try
            {
                await WaitFor(() => events.Contains("reconnected"));
            }
            catch (TimeoutException)
            {
                var types = string.Join(",", _platform.Received.Select(envelope => envelope.GetProperty("type").GetString()));
                throw new TimeoutException($"events=[{string.Join(",", events)}] sockets={_platform.SocketCount} received=[{types}]");
            }
            Assert.Contains("disconnected", events);
            Assert.Equal(2, _platform.SocketCount);
            Assert.Contains(_platform.Received, envelope =>
                envelope.GetProperty("type").GetString() == "resume_session" &&
                envelope.GetProperty("sessionId").GetString() == started.GetProperty("sessionId").GetString() &&
                envelope.GetProperty("sessionToken").GetString() == started.GetProperty("sessionToken").GetString());

            var turn = agent.TurnAsync(new TurnOptions { Timeout = TimeSpan.FromSeconds(5) });
            await _platform.SendAsync(
                $$"""
                {
                  "type": "turn_result",
                  "sessionId": "{{agent.SessionId}}",
                  "ok": true,
                  "action": "move",
                  "args": { "dx": 1 },
                  "result": { "dx": 1 },
                  "attempts": 1,
                  "usage": { "promptTokens": 3, "completionTokens": 2 }
                }
                """);
            var result = await turn;
            Assert.Equal("move", result.Action);
            Assert.Equal(1, result.Args.GetProperty("dx").GetInt32());
            Assert.Equal(1, result.Result.GetProperty("dx").GetInt32());
            Assert.Equal(1, result.Attempts);
            Assert.Equal(3, result.Usage.PromptTokens);
            Assert.Equal(2, result.Usage.CompletionTokens);
        }
        finally
        {
            await agents.CloseAsync();
            if (_platform != null)
            {
                await _platform.DisposeAsync();
                _platform = null;
            }
        }
    }

    private static async Task WaitFor(Func<bool> ready)
    {
        var start = DateTime.UtcNow;
        while (!ready())
        {
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("timed out");
            }

            await Task.Delay(10);
        }
    }

    private sealed class Pawn
    {
        public object See() => new { };

        public object Move(int dx) => new { dx };
    }

    private sealed class FakePlatform : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<SocketHolder> _sockets = new();
        private readonly string? _code;
        private readonly string? _message;
        private readonly Task _accept;

        private FakePlatform(int port, string? code, string? message)
        {
            _code = code;
            _message = message;
            Url = $"ws://127.0.0.1:{port}/";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _accept = Task.Run(AcceptLoop);
        }

        public string Url { get; }

        public List<JsonElement> Received { get; } = new();

        public int SocketCount
        {
            get
            {
                lock (_sockets)
                {
                    return _sockets.Count;
                }
            }
        }

        public static Task<FakePlatform> StartAsync(string? code = null, string? message = null) =>
            Task.FromResult(new FakePlatform(FreePort(), code, message));

        public void AbortFirstSocket()
        {
            SocketHolder? holder;
            lock (_sockets)
            {
                holder = _sockets.Count == 0 ? null : _sockets[0];
            }

            if (holder == null)
            {
                return;
            }

            try
            {
                holder.Socket.Abort();
            }
            catch (Exception)
            {
                // already closed
            }

            try
            {
                holder.Context.Response.Abort();
            }
            catch (Exception)
            {
                // already closed
            }

            holder.Socket.Dispose();
        }

        public async Task SendAsync(string json)
        {
            WebSocket? socket;
            lock (_sockets)
            {
                socket = _sockets.Count == 0 ? null : _sockets[^1].Socket;
            }

            if (socket == null || socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("fake platform has no open socket");
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            lock (_sockets)
            {
                foreach (var holder in _sockets)
                {
                    try
                    {
                        holder.Socket.Abort();
                        holder.Socket.Dispose();
                    }
                    catch (Exception)
                    {
                        // already closed
                    }
                }
            }

            _listener.Stop();
            try
            {
                await _accept;
            }
            catch (Exception)
            {
                // listener stopped
            }

            _stop.Dispose();
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                var accepted = await context.AcceptWebSocketAsync(null);
                lock (_sockets)
                {
                    _sockets.Add(new SocketHolder(accepted.WebSocket, context));
                }

                _ = ReadLoop(accepted.WebSocket);
            }
        }

        private async Task ReadLoop(WebSocket socket)
        {
            var buffer = new byte[8192];
            try
            {
                while (socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
                {
                    using var stream = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        stream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    using var document = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
                    var message = document.RootElement.Clone();
                    lock (Received)
                    {
                        Received.Add(message);
                    }

                    await ReplyAsync(socket, message);
                }
            }
            catch (Exception)
            {
                // socket aborted
            }
        }

        private async Task ReplyAsync(WebSocket socket, JsonElement message)
        {
            var type = message.GetProperty("type").GetString();
            string? json = type switch
            {
                "hello" when _code != null => JsonSerializer.Serialize(new
                {
                    type = "hello_err",
                    code = _code,
                    message = _message,
                }),
                "hello" => JsonSerializer.Serialize(new
                {
                    type = "hello_ok",
                    accountName = "fake",
                    protocolVersion = ProtocolConstants.ProtocolVersion,
                }),
                "start_session" => JsonSerializer.Serialize(new
                {
                    type = "session_started",
                    sessionId = message.GetProperty("sessionId").GetString(),
                }),
                "resume_session" => JsonSerializer.Serialize(new
                {
                    type = "session_resumed",
                    sessionId = message.GetProperty("sessionId").GetString(),
                }),
                "stop_session" => JsonSerializer.Serialize(new
                {
                    type = "session_stopped",
                    sessionId = message.GetProperty("sessionId").GetString(),
                }),
                "ping" => JsonSerializer.Serialize(new
                {
                    type = "pong",
                    id = message.GetProperty("id").GetString(),
                }),
                _ => null,
            };
            if (json == null || socket.State != WebSocketState.Open)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private sealed class SocketHolder
        {
            public SocketHolder(WebSocket socket, HttpListenerContext context)
            {
                Socket = socket;
                Context = context;
            }

            public WebSocket Socket { get; }

            public HttpListenerContext Context { get; }
        }
    }
}
