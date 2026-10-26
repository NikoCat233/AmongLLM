# AmongLLM MCP Server

The server exposes the official MCP Streamable HTTP transport and a separate WebSocket bridge for Among Us mod instances.

## Run

Requires the .NET 10 SDK/runtime.

```powershell
dotnet restore
dotnet run --no-launch-profile
```

The server binds only to loopback. It tries `127.0.0.1:26767` first and falls back to `127.0.0.1:26768` when that address is occupied. If both ports are unavailable, startup fails. MCP is available at `/mcp`; game plugins connect to `/game/ws`. `/healthz` reports the selected port, protocol version, and process `serverId`, which is also included in the plugin registration acknowledgement. The server accepts requests with loopback hosts and rejects browser origins hosted outside loopback.

Register an HTTP MCP server at either `http://127.0.0.1:26767/mcp` or `http://127.0.0.1:26768/mcp`, depending on the startup message.

## Game bridge protocol v1

The first WebSocket frame must be a JSON registration:

```json
{"type":"register","protocolVersion":1,"instanceId":"00000000-0000-0000-0000-000000000001","processId":1234,"workingDirectory":"D:\\Game\\20260929","gameVersion":"2026.9.29","modVersion":"1.0.0","platformType":"Itch","capabilities":["capture_screenshot","click_sequence"]}
```

The server responds with `registered`, its `serverId`, and the instance ID. A rejected registration receives `{ "type":"error", "error":{"code":"...","message":"..."} }` before the server closes the socket. Plugins send `heartbeat` frames with a monotonically increasing `seq` and a structured `state`. The server expires instances after 15 seconds without a heartbeat and sends a `ping` every five seconds. A plugin may answer with `pong`.

Registration requires a positive `processId` and a fully qualified `workingDirectory`. Both are retained in `list_instances` / `get_game_status` so agents can manage the correct process and checkout; the connection log includes them as structured fields.

Commands have a unique `id`, `operation`, `payload`, and UTC `deadlineUtc`. Plugins answer with a matching `response` frame whose `error` is `{ "code":"...", "message":"..." }` on failure. Long operations may send matching `progress` frames with `progress`, optional `total`, and `message`. Commands are serialized per game instance, including across reconnects, and the deadline includes time spent waiting for that slot. A timeout or client cancellation sends `{ "type":"cancel", "id":"...", "reason":"timeout|client" }` when the socket allows it. The plugin should stop queued or active work with that ID. Cancellation is best effort after an operation has started, and the server never replays an action.

Incoming WebSocket text messages are limited to 32 MiB so screenshot PNG responses larger than 1 MiB can pass. The server does not log command payloads or response bodies.

`click_sequence` uses ordered steps:

```json
[
  {"kind":"click","selector":{"name":"ContinueButton"},"timeoutMs":5000},
  {"kind":"wait","milliseconds":500},
  {"kind":"waitForButton","selector":{"label":"Online"},"timeoutMs":10000},
  {"kind":"click","selector":{"label":"Online"},"timeoutMs":5000}
]
```

`set_text` targets a control listed by `get_screen` and carries its `screenId`, Unity control instance ID, and text. The plugin checks that the same screen and active `TextBoxTMP` or `TMP_InputField` still own the control before changing it.

Agents should specify `timeoutMs` on each `click` and `waitForButton` step to state the maximum wait for a missing button. Omitted values default to **5000 ms**; explicit values may be 1–60000 ms. A `click` step waits for its button before clicking, and an unavailable button stops the sequence when its step limit or the overall deadline expires.

`read_game_logs` returns Unity and BepInEx log entries with sequence numbers. Use `afterSequence` for incremental reads and optional `sources` / `levels` filters. Each response also includes ring-buffer cursor details and the path or error for the local JSONL mirror. The server only reads logs when this tool is called; it does not poll automatically.

`send_rpc` accepts `targetNetId`, `callId`, `sendOption`, optional `targetClientId`, and an ordered `fields` array. `send_hazel_message` accepts a complete structured Hazel envelope as its `message` argument. RPC mode contains `targetNetId` and `callId`:

```json
{"mode":"rpc","targetNetId":123,"callId":5,"sendOption":"Reliable","targetClientId":-1,"fields":[{"type":"packedInt32","value":-2},{"type":"string","value":"hello"}]}
```

Raw mode contains the ordered writer instructions, including every `start`/`end` tag and any protocol-specific fields such as a room game ID:

```json
{"mode":"raw","sendOption":"Reliable","fields":[{"type":"start","tag":5},{"type":"int32","value":123},{"type":"start","tag":6},{"type":"packedUInt32","value":12},{"type":"bytes","value":"AAEC"},{"type":"end"},{"type":"end"}]}
```

Typed fields support `bool`, `byte`, `sbyte`, `int16`, `uint16`, `int32`, `uint32`, `packedInt32`, `packedUInt32`, `float`, `string`, `vector2`, and base64 `bytes`. Raw `start` and `end` tags must be balanced and may nest up to 16 levels. The game plugin builds the writer on its Unity main thread.

## Tools

- Instance and state: `list_instances`, `get_game_status`
- Display and UI: `capture_game_screenshot`, `set_window_state`, `get_screen`, `set_text`, `click_button`, `click_sequence`
- Account: `get_account_info`, `set_itch_auth_token_and_login`
- Game actions: `send_chat_message`, `send_rpc`, `send_hazel_message`, `snap_to`
- Object and code inspection: `list_net_objects`, `read_net_object_properties`, `write_net_object_property`, `execute_csharp`
- Diagnostics: `list_loaded_mods`, `read_game_logs`

## Server logs

The server writes readable console logs as `[HH:mm:ss] [Level] message`, using local time and omitting category names, event IDs, and duplicate scopes. Levels and command outcomes use ANSI colors on an interactive console. Colors are disabled when standard output is redirected, `NO_COLOR` is set, or `TERM=dumb`; set `Logging:Console:FormatterOptions:ColorBehavior` to `Enabled` to force color for redirected output, or `Disabled` to turn it off. On Windows, redirected ANSI output also requires `DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION=1`. `NO_COLOR` and `TERM=dumb` still disable ANSI output. Control characters in log text are escaped. Each line of a multiline message or exception has the same prefix. .NET framework and MCP library logs default to Warning and above, hiding routine hosting and request messages while retaining diagnostics. These levels can be adjusted in `appsettings.json` beside the server assembly; configuration is loaded from that directory regardless of the working directory used to launch the server.

Application logs cover startup, port fallback, WebSocket acceptance, registration/reconnection/disconnection, and command completion/error/timeout/transport failures. Routine queued, sent, and cancellation events are Debug-level; successful completions are Information-level, while actionable failures remain visible as warnings or errors. Command events include the instance, process, command ID, operation, elapsed time, outcome, and bounded payload/result summaries. RPC and Hazel summaries show target IDs, send options, typed scalar/vector fields, and bounded string values; SnapTo summaries show the network object ID and coordinates. Byte payload contents, credentials, code, chat/text contents, screenshot data, game log entries, and arbitrary inspection results remain omitted or redacted. Plugin error messages are sanitized and included to make failures actionable.

The Itch account tool passes the token through the bridge without server-side logging. It can complete the first-sign-in account-creation and friend-code prompts. If the game requests age verification, it uses the actual `dateOfBirth` supplied by the user (`2000-04-04` by default), formatted as `yyyy-MM-dd`; the accepted range is 1901 through the previous UTC calendar year. Passing `null` sends an explicit null and disables automated age verification. The tool does not guess a date or bypass age checks. `execute_csharp` executes code inside the game process and can read credentials, hang, or crash that process. Use it only with trusted snippets.

## Tests

```powershell
dotnet test .\tests\tests.csproj
```

Tests use fake WebSocket game clients; they do not launch Among Us.
