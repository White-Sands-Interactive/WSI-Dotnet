using WhiteSands.AgentPlayer.Mcp;

namespace WhiteSands.AgentPlayer.Testing;

/// <summary>
/// In-process platform that replays a script of tool calls.
/// Same session loop as production; no network and no platform key.
/// </summary>
public static class TestPlatform
{
    public static IInlinePlatform Create(IReadOnlyList<ToolCallRequest> script) =>
        Create(new ScriptedModel(script));

    public static IInlinePlatform Create(IModelClient model) => new Platform(model);

    private sealed class Platform : IInlinePlatform
    {
        private readonly IModelClient _model;
        private readonly Dictionary<string, SessionLoop> _loops = new();

        public Platform(IModelClient model)
        {
            _model = model;
        }

        public async Task StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
        {
            var loop = new SessionLoop(new SessionLoopOptions(
                request.SessionId,
                request.Transport,
                request.Instructions,
                _model,
                request.OnTurnResult,
                request.Goal,
                request.Logger,
                request.OnUsage,
                evt => request.OnEvent?.Invoke(LoopEvents.ToAgentEvent(request.SessionId, evt))));
            _loops[request.SessionId] = loop;
            await loop.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task StopSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (!_loops.TryGetValue(sessionId, out var loop))
            {
                return;
            }

            _loops.Remove(sessionId);
            await loop.StopAsync().ConfigureAwait(false);
        }

        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            var loops = _loops.Values.ToList();
            _loops.Clear();
            foreach (var loop in loops)
            {
                await loop.StopAsync().ConfigureAwait(false);
            }
        }
    }
}
