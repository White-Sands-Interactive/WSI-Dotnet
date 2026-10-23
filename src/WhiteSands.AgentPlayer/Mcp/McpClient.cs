using System.Text.Json;
using System.Text.Json.Nodes;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Mcp;

public sealed class McpToolInfo
{
    public McpToolInfo(string name, string description, JsonElement inputSchema)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
    }

    public string Name { get; }

    public string Description { get; }

    public JsonElement InputSchema { get; }
}

public sealed class ToolPayload
{
    public ToolPayload(bool isError, string text, JsonElement json)
    {
        IsError = isError;
        Text = text;
        Json = json;
    }

    public bool IsError { get; }

    public string Text { get; }

    public JsonElement Json { get; }
}

/// <summary>Minimal MCP client used by the offline session loop. The hosted platform has its own.</summary>
public sealed class McpClient
{
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _gate = new();
    private IMcpTransport? _transport;
    private int _nextId;
    private bool _closed;

    public async Task ConnectAsync(IMcpTransport transport, CancellationToken cancellationToken = default)
    {
        _transport = transport;
        transport.OnMessage(Deliver);
        await transport.StartAsync(cancellationToken).ConfigureAwait(false);
        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolConstants.McpProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "agent-player",
                ["version"] = ProtocolConstants.SdkVersion,
            },
        }, cancellationToken).ConfigureAwait(false);
        await transport.SendAsync(JsonRpc.Notify("notifications/initialized", null), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var tools = new List<McpToolInfo>();
        if (!result.TryGetProperty("tools", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return tools;
        }

        foreach (var tool in array.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString() ?? "";
            var description = tool.TryGetProperty("description", out var described) && described.ValueKind == JsonValueKind.String
                ? described.GetString() ?? name
                : name;
            var schema = tool.TryGetProperty("inputSchema", out var input) ? input.Clone() : ProtocolJson.EmptyObject();
            tools.Add(new McpToolInfo(name, description, schema));
        }

        return tools;
    }

    public async Task<ToolPayload> CallToolAsync(
        string name,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            arguments = ProtocolJson.EmptyObject();
        }

        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = JsonNode.Parse(arguments.GetRawText()),
        }, cancellationToken).ConfigureAwait(false);
        return ParsePayload(result);
    }

    public async Task CloseAsync()
    {
        List<TaskCompletionSource<JsonElement>> waiters;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            waiters = _pending.Values.ToList();
            _pending.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetException(new InvalidOperationException("mcp client closed"));
        }

        if (_transport != null)
        {
            try
            {
                await _transport.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ignore
            }
        }
    }

    private async Task<JsonElement> RequestAsync(string method, JsonNode? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var key = JsonRpc.NumberId(id);
        var source = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed)
            {
                throw new InvalidOperationException("mcp client closed");
            }

            _pending[key] = source;
        }

        try
        {
            var transport = _transport ?? throw new InvalidOperationException("Client not connected");
            await transport.SendAsync(JsonRpc.Request(id, method, parameters), cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() =>
            {
                lock (_gate)
                {
                    _pending.Remove(key);
                }

                source.TrySetCanceled(cancellationToken);
            });
            return await source.Task.ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _pending.Remove(key);
            }

            throw;
        }
    }

    private void Deliver(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var method = message.TryGetProperty("method", out var methodProperty) ? methodProperty.GetString() : null;
        var hasId = message.TryGetProperty("id", out var id) &&
                    id.ValueKind != JsonValueKind.Null &&
                    id.ValueKind != JsonValueKind.Undefined;
        if (!string.IsNullOrEmpty(method))
        {
            if (!hasId || _transport == null)
            {
                return;
            }

            var response = method == "ping"
                ? JsonRpc.Response(id, new JsonObject())
                : JsonRpc.Error(id, -32601, "method not found: " + method);
            _ = _transport.SendAsync(response);
            return;
        }

        if (!hasId)
        {
            return;
        }

        var key = JsonRpc.IdKey(id);
        TaskCompletionSource<JsonElement>? source;
        lock (_gate)
        {
            if (!_pending.TryGetValue(key, out source))
            {
                return;
            }

            _pending.Remove(key);
        }

        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var text = error.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() : "mcp error";
            source.TrySetException(new InvalidOperationException(text ?? "mcp error"));
            return;
        }

        if (message.TryGetProperty("result", out var result))
        {
            source.TrySetResult(result.Clone());
            return;
        }

        source.TrySetException(new InvalidOperationException("mcp response missing result"));
    }

    private static ToolPayload ParsePayload(JsonElement result)
    {
        var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
        var parts = new List<string>();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in content.EnumerateArray())
            {
                if (entry.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                    entry.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? "");
                }
            }
        }

        var joined = string.Join("\n", parts);
        JsonElement json;
        try
        {
            json = ProtocolJson.Parse(joined);
        }
        catch (JsonException)
        {
            json = ProtocolJson.ToElement(joined);
        }

        return new ToolPayload(isError, joined, json);
    }
}
