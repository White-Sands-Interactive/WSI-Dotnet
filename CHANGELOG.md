# Changelog

## 1.1.0

- The SDK always connects to `wss://api.whitesandsint.com`. `WSI_PLATFORM_URL` and `PLATFORM_URL` are no longer read.

## 1.0.0

First release of the C# agent player SDK. It speaks the same protocol as `@whitesands/agent-player` 1.0.0.

- `AgentServer` reads `WSI_PLATFORM_KEY` and `WSI_PLATFORM_URL`.
- Player classes become tools. Argument types come from the method signature.
- `TurnAsync` returns the action, arguments, result, attempt count, and token usage.
- Typed exceptions, event listeners, turn timeouts, and `await using`.
- Reconnect with session resume, heartbeat, and a protocol version handshake.
- `TestPlatform` runs the production loop offline.
- Action history is in memory by default. `WhiteSands.AgentPlayer.Sqlite` writes `.wsi/agent-memory.sqlite`.
