using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AmongLLM.Server.Transport;

namespace AmongLLM.Server.Mcp;

[McpServerToolType]
public sealed class GameTools
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "list_instances", ReadOnly = true)]
    [Description("List connected and recently disconnected Among Us game instances registered with this local server.")]
    public static string ListInstances(GameInstanceRegistry registry) =>
        JsonSerializer.Serialize(registry.ListInstances(), Json);

    [McpServerTool(Name = "get_game_status", ReadOnly = true)]
    [Description("Return the latest structured status reported by a selected game instance.")]
    public static string GetGameStatus(
        [Description("Registered game instance GUID.")] string instanceId,
        GameInstanceRegistry registry) =>
        JsonSerializer.Serialize(registry.GetInstance(instanceId), Json);

    [McpServerTool(Name = "capture_game_screenshot", ReadOnly = true)]
    [Description("Capture the selected game's current rendered frame and return it as an MCP image.")]
    public static async Task<CallToolResult> CaptureScreenshot(
        [Description("Registered game instance GUID.")] string instanceId,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken)
    {
        var result = await Dispatch(registry, instanceId, "capture_screenshot", new { }, DefaultTimeout, null, cancellationToken);
        var encoded = ReadString(result, "pngBase64", "imageBase64", "base64", "data");
        if (encoded is null)
            throw new McpException("The game plugin response did not contain a base64 PNG image.");

        byte[] image;
        try
        {
            image = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new McpException("The game plugin returned invalid base64 image data.");
        }

        return new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(image, "image/png"),
                new TextContentBlock { Text = $"Captured {image.Length} bytes from game instance {instanceId}." }
            ]
        };
    }

    [McpServerTool(Name = "set_window_state", Destructive = true)]
    [Description("Show and foreground, restore, or minimize a registered Among Us window by its verified process ID.")]
    public static string SetWindowState(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("One of: foreground, restore, background.")] string state,
        GameInstanceRegistry registry)
    {
        var instance = registry.GetInstance(instanceId);
        if (!OperatingSystem.IsWindows())
            throw new McpException("Window control is available only on Windows.");
        if (!instance.Online)
            throw new McpException("The selected game instance is offline.");
        if (state is not ("foreground" or "restore" or "background"))
            throw new McpException("state must be foreground, restore, or background.");

        using var process = Process.GetProcessById(instance.ProcessId);
        if (process.HasExited || !(Path.GetFileName(process.MainModule?.FileName) ?? "").Equals("Among Us.exe", StringComparison.OrdinalIgnoreCase))
            throw new McpException("The registered process is no longer the expected Among Us executable.");

        var handle = process.MainWindowHandle;
        if (handle == IntPtr.Zero)
            throw new McpException("The registered game process has no main window handle.");

        if (state == "background")
        {
            NativeWindow.ShowWindow(handle, NativeWindow.SwMinimize);
        }
        else
        {
            NativeWindow.ShowWindow(handle, NativeWindow.SwRestore);
            if (state == "foreground" && !NativeWindow.SetForegroundWindow(handle))
                throw new McpException("Windows did not allow the game window to become foreground.");
        }

        return JsonSerializer.Serialize(new { instanceId, state, processId = instance.ProcessId }, Json);
    }

    [McpServerTool(Name = "get_screen", ReadOnly = true)]
    [Description("Return structured active-screen information and the current clickable button inventory.")]
    public static async Task<string> GetScreen(
        [Description("Registered game instance GUID.")] string instanceId,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(await Dispatch(registry, instanceId, "get_screen", new { }, DefaultTimeout, null, cancellationToken), Json);

    [McpServerTool(Name = "set_text", Destructive = true)]
    [Description("Set text in the currently active game text control identified by get_screen. The plugin revalidates the screen and control before writing.")]
    public static async Task<string> SetText(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Screen snapshot ID returned by get_screen.")] string screenId,
        [Description("Unity text control instance ID returned by get_screen.")] int textBoxInstanceId,
        [Description("Text to place in the active TextBoxTMP or TMP_InputField.")] string text,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 8192)
            throw new McpException("text must contain 1 to 8192 characters.");
        var payload = new { screenId, instanceId = textBoxInstanceId, text };
        return JsonSerializer.Serialize(await Dispatch(registry, instanceId, "set_text", payload, DefaultTimeout, null, cancellationToken), Json);
    }

    [McpServerTool(Name = "click_button", Destructive = true)]
    [Description("Click one currently visible button by ID and screen snapshot ID. The plugin revalidates the target before dispatch.")]
    public static async Task<string> ClickButton(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Button ID returned by get_screen.")] string buttonId,
        [Description("Screen snapshot ID returned by get_screen.")] string screenId,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(await Dispatch(registry, instanceId, "click_button", new { buttonId, screenId }, DefaultTimeout, null, cancellationToken), Json);

    [McpServerTool(Name = "click_sequence", Destructive = true)]
    [Description("Run ordered click, fixed-wait, and wait-for-button steps. Specify each click/waitForButton step's timeoutMs explicitly when choosing how long an absent button may be awaited; omitted timeoutMs defaults to 5000 ms. A missing button stops the sequence at that bound. Selectors are resolved again at execution time.")]
    public static async Task<string> ClickSequence(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Ordered steps: {kind:'click',selector:{...},timeoutMs:5000}, {kind:'wait',milliseconds:n}, or {kind:'waitForButton',selector:{...},timeoutMs:5000}. Per-button timeout range: 1..60000 ms; default 5000 ms.")] JsonElement steps,
        [Description("Overall sequence timeout in milliseconds; allowed range is 1000 through 180000.")] int timeoutMs,
        GameInstanceRegistry registry,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 1 or > 64)
            throw new McpException("steps must be an array containing 1 to 64 steps.");
        if (timeoutMs is < 1000 or > 180000)
            throw new McpException("timeoutMs must be between 1000 and 180000.");
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object || !step.TryGetProperty("kind", out var kind)) throw new McpException("Each step requires kind.");
            if (kind.GetString() == "wait")
            {
                if (!step.TryGetProperty("milliseconds", out var milliseconds) || !milliseconds.TryGetInt32(out var value) || value is < 0 or > 60000) throw new McpException("Fixed waits must specify 0..60000 milliseconds.");
            }
            else if (kind.GetString() is "click" or "waitForButton")
            {
                if (!step.TryGetProperty("selector", out var selector) || selector.ValueKind != JsonValueKind.Object) throw new McpException("Button steps require an object selector.");
                if (step.TryGetProperty("timeoutMs", out var buttonTimeout) && (!buttonTimeout.TryGetInt32(out var value) || value is < 1 or > 60000)) throw new McpException("Per-button timeoutMs must be 1..60000 milliseconds, or omitted for the 5000 ms default.");
            }
            else throw new McpException("Step kind must be click, wait, or waitForButton.");
        }

        var result = await Dispatch(registry, instanceId, "click_sequence", new { steps, timeoutMs },
            TimeSpan.FromMilliseconds(timeoutMs), progress, cancellationToken);
        return JsonSerializer.Serialize(result, Json);
    }

    [McpServerTool(Name = "get_account_info", ReadOnly = true)]
    [Description("Return EOS login status, PUID, friend code, platform type, and optionally the sensitive EOS user ID token.")]
    public static async Task<string> GetAccountInfo(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Set true only when the caller needs the bearer token. The token is never logged by this server.")] bool includeUserIdToken,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(await Dispatch(registry, instanceId, "get_account_info", new { includeUserIdToken }, DefaultTimeout, null, cancellationToken), Json);

    [McpServerTool(Name = "set_itch_auth_token_and_login", Destructive = true)]
    [Description("On an Itch client only, replace EOSManager.platformAuthToken with the supplied Itch API key and call LoginWithCorrectPlatform. Automatically handle first-sign-in account creation, login-success confirmation and friend-code registration. If age verification is requested, dateOfBirth defaults to the user's supplied date, 2000-04-04, and is submitted through the game's native validation. Never call for other platform types.")]
    public static async Task<string> SetItchAuthTokenAndLogin(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Itch API key to use as the platform auth token. Do not include this value in logs or prompts.")] string platformAuthToken,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken,
        [Description("Actual date of birth for an age-verification prompt, formatted yyyy-MM-dd. Defaults to the date the user supplied, 2000-04-04. Must be a real date from 1901 through last year in UTC. Pass null to disable automatic age submission. Never provide an estimated or fabricated date.")] string? dateOfBirth = "2000-04-04")
    {
        if (string.IsNullOrWhiteSpace(platformAuthToken) || platformAuthToken.Length > 8192)
            throw new McpException("platformAuthToken must contain 1 to 8192 characters.");

        if (dateOfBirth is not null && !IsSupportedDateOfBirth(dateOfBirth))
            throw new McpException("dateOfBirth must be a valid real date formatted yyyy-MM-dd, from 1901 through last year in UTC.");

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["platformAuthToken"] = platformAuthToken,
            ["dateOfBirth"] = dateOfBirth
        };

        var result = await Dispatch(registry, instanceId, "set_itch_auth_token_and_login",
            payload, TimeSpan.FromSeconds(95), null, cancellationToken);
        return JsonSerializer.Serialize(result, Json);
    }

    private static bool IsSupportedDateOfBirth(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
        date.Year is >= 1901 && date.Year <= DateTime.UtcNow.Year - 1;

    [McpServerTool(Name = "send_chat_message", Destructive = true)]
    [Description("Send a chat message as the local player using the game's chat RPC.")]
    public static async Task<string> SendChatMessage(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Chat text to send.")] string message,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 1000)
            throw new McpException("message must contain 1 to 1000 characters.");
        return JsonSerializer.Serialize(await Dispatch(registry, instanceId, "send_chat_message", new { message }, DefaultTimeout, null, cancellationToken), Json);
    }

    [McpServerTool(Name = "send_rpc", Destructive = true)]
    [Description("Send a typed RPC through StartRpcImmediately/FinishRpcImmediately. fields is ordered; supported types include bool, byte, sbyte, int16, uint16, int32, uint32, packedInt32, packedUInt32, float, string, vector2, and base64 bytes.")]
    public static async Task<string> SendRpc(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Target network object ID.")] uint targetNetId,
        [Description("RPC call ID (0 through 255).")] byte callId,
        [Description("Reliable or Unreliable.")] string sendOption,
        [Description("Ordered list of typed writer fields.")] JsonElement fields,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken,
        [Description("Optional target client ID. Omit or use -1 to broadcast.")] int targetClientId = -1)
    {
        ValidateRpcEnvelope(targetNetId, callId, sendOption, fields);
        var payload = new { targetNetId, callId, sendOption, targetClientId, fields };
        return JsonSerializer.Serialize(await Dispatch(registry, instanceId, "send_rpc", payload, DefaultTimeout, null, cancellationToken), Json);
    }

    [McpServerTool(Name = "send_hazel_message", Destructive = true)]
    [Description("Send a structured Hazel message. message.mode is 'rpc' with targetNetId/callId, or 'raw' with complete writer fields including balanced start/end tags and the gameId int32 field when required by the target protocol. Fields are ordered typed values; bytes use base64.")]
    public static async Task<string> SendHazelMessage(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Complete Hazel envelope: {mode:'rpc',targetNetId,callId,sendOption,targetClientId?,fields:[...]} or {mode:'raw',sendOption,fields:[...]}.") ] JsonElement message,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken)
    {
        ValidateHazelEnvelope(message);
        return JsonSerializer.Serialize(await Dispatch(registry, instanceId, "send_hazel_message", message.Clone(), DefaultTimeout, null, cancellationToken), Json);
    }

    [McpServerTool(Name = "snap_to", Destructive = true)]
    [Description("Send a SnapTo operation for a player object. A room host may target any player; non-host behavior is validated by the plugin.")]
    public static async Task<string> SnapTo(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Network object ID of the player to move.")] uint netObjectId,
        [Description("X map coordinate.")] float x,
        [Description("Y map coordinate.")] float y,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > 1000 || Math.Abs(y) > 1000)
            throw new McpException("Coordinates must be finite and within the supported range.");
        return JsonSerializer.Serialize(await Dispatch(registry, instanceId, "snap_to", new { netObjectId, x, y }, DefaultTimeout, null, cancellationToken), Json);
    }

    [McpServerTool(Name = "list_net_objects", ReadOnly = true)]
    [Description("Enumerate network objects in the selected room with their network IDs and runtime types.")]
    public static async Task<string> ListNetObjects(
        [Description("Registered game instance GUID.")] string instanceId,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(await Dispatch(registry, instanceId, "list_net_objects", new { }, LongTimeout, null, cancellationToken), Json);

    [McpServerTool(Name = "read_net_object_properties", ReadOnly = true)]
    [Description("Read selected members from one network object by network ID.")]
    public static async Task<string> ReadNetObjectProperties(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Network object ID.")] uint netObjectId,
        [Description("Optional member paths to read; omit to return readable members.")] string[]? members,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(await Dispatch(registry, instanceId, "read_net_object_properties", new { netObjectId, members }, LongTimeout, null, cancellationToken), Json);

    [McpServerTool(Name = "write_net_object_property", Destructive = true)]
    [Description("Write a member on a network object. Room-host permission and member/type validation are enforced by the plugin.")]
    public static async Task<string> WriteNetObjectProperty(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("Network object ID.")] uint netObjectId,
        [Description("Member path to write.")] string member,
        [Description("JSON value to assign.")] JsonElement value,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(await Dispatch(registry, instanceId, "write_net_object_property", new { netObjectId, member, value }, LongTimeout, null, cancellationToken), Json);

    [McpServerTool(Name = "execute_csharp", Destructive = true, OpenWorld = true)]
    [Description("Execute a C# fragment inside the selected game process. This is privileged in-process code execution and may read credentials or freeze or crash the game.")]
    public static async Task<string> ExecuteCSharp(
        [Description("Registered game instance GUID.")] string instanceId,
        [Description("C# fragment to execute in the game process.")] string code,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 32_000)
            throw new McpException("code must contain 1 to 32000 characters.");
        return JsonSerializer.Serialize(await Dispatch(registry, instanceId, "execute_csharp", new { code }, LongTimeout, null, cancellationToken), Json);
    }

    [McpServerTool(Name = "list_loaded_mods", ReadOnly = true)]
    [Description("List mods loaded by BepInEx, including plugin ID, display name, version, and assembly path when available.")]
    public static async Task<string> ListLoadedMods(
        [Description("Registered game instance GUID.")] string instanceId,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(await Dispatch(registry, instanceId, "list_loaded_mods", new { }, LongTimeout, null, cancellationToken), Json);

    [McpServerTool(Name = "read_game_logs", ReadOnly = true)]
    [Description("Read recent Unity and BepInEx log entries from the selected game. Use afterSequence for incremental reads; the server does not poll logs automatically.")]
    public static async Task<string> ReadGameLogs(
        [Description("Registered game instance GUID.")] string instanceId,
        GameInstanceRegistry registry,
        CancellationToken cancellationToken,
        [Description("Return entries after this sequence number; defaults to 0.")] long afterSequence = 0,
        [Description("Maximum entries to return, from 1 to 1000; defaults to 200.")] int limit = 200,
        [Description("Optional source filters, such as Unity or a BepInEx plugin source.")] string[]? sources = null,
        [Description("Optional log-level filters.")] string[]? levels = null)
    {
        if (afterSequence < 0)
            throw new McpException("afterSequence must be zero or greater.");
        if (limit is < 1 or > 1000)
            throw new McpException("limit must be between 1 and 1000.");

        var payload = new Dictionary<string, object?>
        {
            ["afterSequence"] = afterSequence,
            ["limit"] = limit
        };
        if (sources is not null)
            payload["sources"] = sources;
        if (levels is not null)
            payload["levels"] = levels;
        return JsonSerializer.Serialize(await Dispatch(registry, instanceId, "read_game_logs", payload, DefaultTimeout, null, cancellationToken), Json);
    }

    private static Task<JsonElement> Dispatch(
        GameInstanceRegistry registry,
        string instanceId,
        string operation,
        object payload,
        TimeSpan timeout,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken) =>
        registry.SendCommandAsync(instanceId, operation, payload, timeout, progress, cancellationToken);

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static void ValidateHazelEnvelope(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
            throw new McpException("message must be a Hazel envelope object.");
        var mode = ReadString(message, "mode");
        var sendOption = ReadString(message, "sendOption");
        if (mode is not ("rpc" or "raw"))
            throw new McpException("message.mode must be rpc or raw.");
        if (sendOption is not ("Reliable" or "Unreliable"))
            throw new McpException("message.sendOption must be Reliable or Unreliable.");

        if (mode == "rpc")
        {
            if (!message.TryGetProperty("targetNetId", out var netId) || !netId.TryGetUInt32(out _) ||
                !message.TryGetProperty("callId", out var callId) || !callId.TryGetByte(out _))
                throw new McpException("An rpc envelope requires uint targetNetId and byte callId.");
            if (message.TryGetProperty("targetClientId", out var targetClientId) && !targetClientId.TryGetInt32(out _))
                throw new McpException("targetClientId must be an int32.");
        }

        ValidateHazelFields(message, mode);
    }

    private static void ValidateRpcEnvelope(uint targetNetId, byte callId, string sendOption, JsonElement fields)
    {
        if (sendOption is not ("Reliable" or "Unreliable"))
            throw new McpException("sendOption must be Reliable or Unreliable.");
        var envelope = JsonSerializer.SerializeToElement(new { mode = "rpc", targetNetId, callId, sendOption, fields }, Json);
        ValidateHazelFields(envelope, "rpc");
    }

    private static void ValidateHazelFields(JsonElement envelope, string mode)
    {
        if (!envelope.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() > 128)
            throw new McpException("fields must be an array with no more than 128 entries.");

        var depth = 0;
        foreach (var field in fields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
                throw new McpException("Each field must be an object with a string type.");
            var type = typeValue.GetString();
            if (type == "start")
            {
                if (mode != "raw" || !field.TryGetProperty("tag", out var tag) || !tag.TryGetByte(out _))
                    throw new McpException("A start field is only valid in raw mode and requires a byte tag.");
                if (++depth > 16)
                    throw new McpException("Raw Hazel start/end nesting cannot exceed 16 levels.");
                continue;
            }
            if (type == "end")
            {
                if (mode != "raw" || --depth < 0)
                    throw new McpException("Raw Hazel end fields must match an earlier start field.");
                continue;
            }

            if (!field.TryGetProperty("value", out var value))
                throw new McpException($"Hazel field '{type}' requires a value.");
            var valid = type switch
            {
                "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "byte" => InRange(value, byte.MinValue, byte.MaxValue),
                "sbyte" => InRange(value, sbyte.MinValue, sbyte.MaxValue),
                "int16" => InRange(value, short.MinValue, short.MaxValue),
                "uint16" => InRange(value, ushort.MinValue, ushort.MaxValue),
                "int32" or "packedInt32" => value.TryGetInt32(out _),
                "uint32" or "packedUInt32" => value.TryGetUInt32(out _),
                "float" => value.TryGetSingle(out var number) && float.IsFinite(number),
                "string" => value.ValueKind == JsonValueKind.String && (value.GetString()?.Length ?? 0) <= 8192,
                "vector2" => IsVector2(value),
                "bytes" => IsByteArray(value),
                _ => false
            };
            if (!valid)
                throw new McpException($"Hazel field '{type}' has an unsupported type or invalid value.");
        }
        if (depth != 0)
            throw new McpException("Raw Hazel start/end fields must be balanced.");
    }

    private static bool InRange(JsonElement value, long minimum, long maximum) =>
        value.TryGetInt64(out var number) && number >= minimum && number <= maximum;

    private static bool IsVector2(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("x", out var x) && x.TryGetSingle(out var xValue) && float.IsFinite(xValue) &&
        value.TryGetProperty("y", out var y) && y.TryGetSingle(out var yValue) && float.IsFinite(yValue);

    private static bool IsByteArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return false;
        try { return Convert.FromBase64String(value.GetString() ?? "").Length <= 65_536; }
        catch (FormatException) { return false; }
    }

    private static class NativeWindow
    {
        public const int SwMinimize = 6;
        public const int SwRestore = 9;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr handle, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr handle);
    }
}
