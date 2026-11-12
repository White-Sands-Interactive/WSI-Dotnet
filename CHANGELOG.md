# Changelog

## 1.3.0

- `StartAsync` requires `PlayerId`, the character UUID from the Players page. One platform key starts every character in that game. A missing or non-UUID id is rejected before the socket opens. A character this key cannot play fails `StartAsync` with the platform error.

## 1.2.0

- `BindDynamic` registers one tool from a name, description, argument schema, and callback. `see` may replace the reflected view. `wait`, an empty name, a name starting with `_`, and a missing callback are rejected.
- `Interrupt` closes the platform socket from any thread. Sessions stay registered, and a turn waiting for a result stays waiting. `CloseAsync` still stops sessions.

## 1.1.1

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
