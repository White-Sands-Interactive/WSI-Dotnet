using System.Text.Json;
using System.Text.Json.Nodes;
using WhiteSands.AgentPlayer.Memory;
using WhiteSands.AgentPlayer.Protocol;
using WhiteSands.AgentPlayer.Reflection;

namespace WhiteSands.AgentPlayer.Mcp;

/// <summary>
/// In-process MCP server for one player session.
/// Tools are see, wait, and every public action.
/// When an action log is set, see and wait include what this character already called.
/// </summary>
public sealed class SessionMcpServer
{
    private readonly ReflectedPlayer _player;
    private readonly TurnGate _gate;
    private readonly string _name;
    private readonly SessionActionLog? _actions;
    private IMcpTransport? _transport;
    private int _closed;

    public SessionMcpServer(ReflectedPlayer player, TurnGate gate, string? name = null, SessionActionLog? actions = null)
    {
        _player = player;
        _gate = gate;
        _name = name ?? "agent-player";
        _actions = actions;
    }

    public void Attach(IMcpTransport transport)
    {
        _transport = transport;
        transport.OnMessage(message =>
        {
            var copy = message.Clone();
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(copy).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    TrySend(JsonRpc.Error(default, -32603, ex.Message));
                }
            });
        });
    }

    public async Task CloseAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
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

    private async Task HandleAsync(JsonElement message)
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return;
        }

        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null;
        var method = message.TryGetProperty("method", out var methodProperty) ? methodProperty.GetString() : null;
        if (method == null)
        {
            return;
        }

        if (!hasId || method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            return;
        }

        JsonElement parameters = default;
        if (message.TryGetProperty("params", out var paramsProperty))
        {
            parameters = paramsProperty;
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    Reply(id, Initialize(parameters));
                    return;
                case "ping":
                    Reply(id, new JsonObject());
                    return;
                case "tools/list":
                    Reply(id, ListTools());
                    return;
                case "tools/call":
                    Reply(id, await CallToolAsync(parameters).ConfigureAwait(false));
                    return;
                default:
                    TrySend(JsonRpc.Error(id, -32601, "method not found: " + method));
                    return;
            }
        }
        catch (Exception ex)
        {
            TrySend(JsonRpc.Error(id, -32603, ex.Message));
        }
    }

    private JsonObject Initialize(JsonElement parameters)
    {
        var version = ProtocolConstants.McpProtocolVersion;
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("protocolVersion", out var requested) &&
            requested.ValueKind == JsonValueKind.String)
        {
            var text = requested.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                version = text!;
            }
        }

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = _name,
                ["version"] = ProtocolConstants.SdkVersion,
            },
        };
    }

    private JsonObject ListTools()
    {
        var tools = new JsonArray();
        foreach (var tool in _player.Tools)
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
            });
        }

        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonObject> CallToolAsync(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out var nameProperty) ||
            nameProperty.ValueKind != JsonValueKind.String)
        {
            return ToolError("missing tool name");
        }

        var name = nameProperty.GetString() ?? "";
        var args = ProtocolJson.EmptyObject();
        if (parameters.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object)
        {
            args = arguments;
        }

        try
        {
            if (name == "see")
            {
                return JsonResult(await ViewedAsync().ConfigureAwait(false));
            }

            if (name == "wait")
            {
                await _gate.WaitForTurnAsync().ConfigureAwait(false);
                return JsonResult(await ViewedAsync().ConfigureAwait(false));
            }

            var action = _player.Actions.FirstOrDefault(candidate => candidate.Name == name);
            if (action == null)
            {
                return ToolError("Unknown tool: " + name);
            }

            try
            {
                var result = await action.InvokeAsync(args).ConfigureAwait(false);
                _actions?.Record(new DoneAction(name, args.Clone(), true));
                return JsonResult(result ?? new JsonObject { ["ok"] = true });
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                _actions?.Record(new DoneAction(name, args.Clone(), false, message));
                return ToolError(message);
            }
        }
        catch (Exception ex)
        {
            return ToolError(ex.Message);
        }
    }

    private async Task<JsonElement> ViewedAsync()
    {
        var view = ProtocolJson.ToElement(await _player.See.InvokeAsync(ProtocolJson.EmptyObject()).ConfigureAwait(false));
        if (_actions == null)
        {
            return view;
        }

        return ActionMemory.ViewWithDone(view, _actions.Done());
    }

    private static JsonObject JsonResult(object? payload, bool isError = false)
    {
        var text = payload is JsonElement element
            ? element.GetRawText()
            : payload is JsonNode node
                ? node.ToJsonString()
                : ProtocolJson.ToElement(payload).GetRawText();
        return new JsonObject
        {
            ["isError"] = isError,
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text,
                },
            },
        };
    }

    private static JsonObject ToolError(string message) =>
        JsonResult(new JsonObject { ["error"] = message }, isError: true);

    private void Reply(JsonElement id, JsonNode result) => TrySend(JsonRpc.Response(id, result));

    private void TrySend(JsonElement message)
    {
        if (Volatile.Read(ref _closed) == 1 || _transport == null)
        {
            return;
        }

        _ = _transport.SendAsync(message);
    }
}

/// <summary>Records and reads the actions already taken in one session.</summary>
public sealed class SessionActionLog
{
    private readonly IActionMemory _memory;
    private readonly string _sessionId;
    private readonly string _character;

    public SessionActionLog(IActionMemory memory, string sessionId, string character)
    {
        _memory = memory;
        _sessionId = sessionId;
        _character = character;
    }

    public void Record(DoneAction entry) => _memory.Record(_sessionId, _character, entry);

    public IReadOnlyList<DoneAction> Done() => _memory.Done(_sessionId);
}
