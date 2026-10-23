using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhiteSands.AgentPlayer.Mcp;
using WhiteSands.AgentPlayer.Protocol;

namespace WhiteSands.AgentPlayer.Testing;

public sealed class LoopEvent
{
    public LoopEvent(string name, string? action = null, JsonElement? args = null, string? error = null)
    {
        Event = name;
        Action = action;
        Args = args;
        Error = error;
    }

    public string Event { get; }

    public string? Action { get; }

    public JsonElement? Args { get; }

    public string? Error { get; }
}

public static class LoopEvents
{
    public static AgentEvent ToAgentEvent(string sessionId, LoopEvent evt)
    {
        if (evt.Event == "action")
        {
            return new ActionEvent(sessionId, evt.Action ?? "unknown", evt.Args ?? ProtocolJson.EmptyObject());
        }

        if (evt.Event == "retry")
        {
            return new RetryEvent(
                sessionId,
                evt.Action ?? "unknown",
                evt.Args ?? ProtocolJson.EmptyObject(),
                evt.Error ?? "action failed");
        }

        return new TurnFailedEvent(sessionId, evt.Error ?? "turn failed", evt.Action, evt.Args);
    }
}

public sealed class SessionLoopOptions
{
    public SessionLoopOptions(
        string sessionId,
        IMcpTransport transport,
        string instructions,
        IModelClient model,
        Action<TurnReport> onTurnResult,
        string? goal = null,
        ILogger? logger = null,
        Func<int, Task>? onUsage = null,
        Action<LoopEvent>? onEvent = null)
    {
        SessionId = sessionId;
        Transport = transport;
        Instructions = instructions;
        Model = model;
        OnTurnResult = onTurnResult;
        Goal = goal;
        Logger = logger ?? NullLogger.Instance;
        OnUsage = onUsage;
        OnEvent = onEvent;
    }

    public string SessionId { get; }

    public IMcpTransport Transport { get; }

    public string Instructions { get; }

    public string? Goal { get; }

    public IModelClient Model { get; }

    public ILogger Logger { get; }

    public Func<int, Task>? OnUsage { get; }

    public Action<TurnReport> OnTurnResult { get; }

    public Action<LoopEvent>? OnEvent { get; }
}

/// <summary>
/// Per-session MCP client loop:
/// wait, read the view, ask the model, one action, retry once on error, report the turn.
/// </summary>
public sealed class SessionLoop
{
    private readonly SessionLoopOptions _options;
    private McpClient? _client;
    private bool _stopped;
    private Task? _run;

    public SessionLoop(SessionLoopOptions options)
    {
        _options = options;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_run != null)
        {
            return;
        }

        var client = new McpClient();
        await client.ConnectAsync(_options.Transport, cancellationToken).ConfigureAwait(false);
        _client = client;
        _run = RunAsync();
    }

    public async Task StopAsync()
    {
        _stopped = true;
        try
        {
            if (_client != null)
            {
                await _client.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // ignore
        }

        _client = null;
        if (_run != null)
        {
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // reported inside the run
            }
        }

        _run = null;
    }

    private async Task RunAsync()
    {
        try
        {
            await RunLoopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_stopped)
            {
                return;
            }

            _options.Logger.LogError($"session {_options.SessionId} crashed: {ex.Message}");
            _options.OnTurnResult(new TurnReport { Ok = false, Error = ex.Message, Attempts = 0 });
        }
    }

    private async Task<ToolPayload> CallToolAsync(string name, JsonElement args)
    {
        var client = _client ?? throw new InvalidOperationException("Client not connected");
        return await client.CallToolAsync(name, args).ConfigureAwait(false);
    }

    private async Task RunLoopAsync()
    {
        if (_client == null)
        {
            return;
        }

        var listed = await _client.ListToolsAsync().ConfigureAwait(false);
        var tools = listed.Select(tool => new ModelTool(tool.Name, tool.Description, tool.InputSchema)).ToArray();
        var actionTools = tools.Where(tool => tool.Name != "wait" && tool.Name != "see").ToArray();

        while (!_stopped)
        {
            var waitResult = await CallToolAsync("wait", ProtocolJson.EmptyObject()).ConfigureAwait(false);
            if (_stopped)
            {
                break;
            }

            if (waitResult.IsError)
            {
                _options.OnTurnResult(new TurnReport { Ok = false, Error = waitResult.Text, Attempts = 0 });
                continue;
            }

            var usage = new Usage();
            var attemptDetails = new List<AttemptRecord>();
            var messages = new List<ModelMessage>
            {
                new UserModelMessage(
                    $"It is your turn. Current view:\n{waitResult.Text}\nCall exactly one action tool."),
            };

            var action = await DecideAsync(actionTools, messages, usage).ConfigureAwait(false);
            ToolPayload? result = null;
            string firstError;
            if (action.Name == "wait" || action.Name == "see")
            {
                firstError = $"{action.Name} is not an action";
            }
            else
            {
                Emit(new LoopEvent("action", action.Name, action.Arguments));
                result = await CallToolAsync(action.Name, action.Arguments).ConfigureAwait(false);
                if (!result.IsError)
                {
                    _options.OnTurnResult(new TurnReport
                    {
                        Ok = true,
                        Action = action.Name,
                        Args = action.Arguments,
                        Result = result.Json,
                        Attempts = 1,
                        Usage = usage.ToUsage(),
                    });
                    continue;
                }

                firstError = ToolErrorMessage(result);
            }

            attemptDetails.Add(new AttemptRecord(action.Name, action.Arguments, firstError));
            _options.Logger.LogWarning(
                $"session {_options.SessionId} action {action.Name} failed: {firstError}; retrying once");
            Emit(new LoopEvent("retry", action.Name, action.Arguments, firstError));

            messages.Add(new AssistantModelMessage($"Called {action.Name}({action.Arguments.GetRawText()})"));
            messages.Add(new ToolModelMessage(action.Name, result?.Text ?? firstError, true));
            messages.Add(new UserModelMessage(string.Join("\n", new[]
            {
                $"Action {action.Name} failed: {firstError}",
                "Retry once with a different legal action (or different arguments).",
                "Do not repeat the same failing call. Do not call wait or see.",
                $"Current view was:\n{waitResult.Text}",
            })));

            var retry = await DecideAsync(actionTools, messages, usage).ConfigureAwait(false);
            if (retry.Name == "wait" || retry.Name == "see")
            {
                var error = $"Retry returned {retry.Name} instead of an action (after: {firstError})";
                Emit(new LoopEvent("turnFailed", error: error));
                _options.OnTurnResult(new TurnReport
                {
                    Ok = false,
                    Error = error,
                    Attempts = 1,
                    AttemptDetails = attemptDetails,
                    Usage = usage.ToUsage(),
                });
                continue;
            }

            Emit(new LoopEvent("action", retry.Name, retry.Arguments));
            result = await CallToolAsync(retry.Name, retry.Arguments).ConfigureAwait(false);
            if (!result.IsError)
            {
                _options.OnTurnResult(new TurnReport
                {
                    Ok = true,
                    Action = retry.Name,
                    Args = retry.Arguments,
                    Result = result.Json,
                    Attempts = 2,
                    Usage = usage.ToUsage(),
                });
                continue;
            }

            var secondError = ToolErrorMessage(result);
            attemptDetails.Add(new AttemptRecord(retry.Name, retry.Arguments, secondError));
            _options.Logger.LogWarning($"session {_options.SessionId} retry {retry.Name} failed: {secondError}");
            Emit(new LoopEvent("turnFailed", retry.Name, retry.Arguments, secondError));
            _options.OnTurnResult(new TurnReport
            {
                Ok = false,
                Error = secondError,
                Attempts = 2,
                AttemptDetails = attemptDetails,
                Usage = usage.ToUsage(),
            });
        }
    }

    private async Task<ToolCallRequest> DecideAsync(
        IReadOnlyList<ModelTool> tools,
        IReadOnlyList<ModelMessage> messages,
        Usage usage)
    {
        var decision = await _options.Model.DecideAsync(new ModelRequest(
            ComposeInstructions(_options.Instructions, _options.Goal),
            tools,
            messages)).ConfigureAwait(false);
        usage.PromptTokens += decision.Usage.PromptTokens;
        usage.CompletionTokens += decision.Usage.CompletionTokens;
        var total = decision.Usage.PromptTokens + decision.Usage.CompletionTokens;
        if (_options.OnUsage != null)
        {
            await _options.OnUsage(total).ConfigureAwait(false);
        }

        return decision.ToolCall;
    }

    private void Emit(LoopEvent evt) => _options.OnEvent?.Invoke(evt);

    private static string ComposeInstructions(string instructions, string? goal)
    {
        if (string.IsNullOrEmpty(goal))
        {
            return instructions;
        }

        return $"{instructions}\n\nYour goal: {goal}";
    }

    private static string ToolErrorMessage(ToolPayload result)
    {
        if (result.Json.ValueKind == JsonValueKind.Object &&
            result.Json.TryGetProperty("error", out var error))
        {
            return error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? ""
                : error.GetRawText();
        }

        return result.Text;
    }

    private sealed class Usage
    {
        public int PromptTokens { get; set; }

        public int CompletionTokens { get; set; }

        public TokenUsage ToUsage() => new(PromptTokens, CompletionTokens);
    }
}
