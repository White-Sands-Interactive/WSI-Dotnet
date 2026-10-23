using System.Text.Json;

namespace WhiteSands.AgentPlayer.Mcp;

/// <summary>JSON-RPC transport used by one player session.</summary>
public interface IMcpTransport
{
    void OnMessage(Action<JsonElement> handler);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task SendAsync(JsonElement message, CancellationToken cancellationToken = default);

    Task CloseAsync();
}
