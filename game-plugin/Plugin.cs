using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AmongUs.Data;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Hazel;
using Il2CppInterop.Runtime;
using InnerNet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace AmongLLM.GamePlugin;

[BepInPlugin(PluginGuid, "AmongLLM", PluginVersion)]
[BepInProcess("Among Us.exe")]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "dev.codex.amongllm";
    public const string PluginVersion = "0.1.0";

    public override void Load()
    {
        RuntimeLogs.Install();
        RuntimeInfo.PlatformType = Constants.GetPlatformType().ToString();
        RuntimeInfo.GameVersion = Application.version;
        AddComponent<BridgeBehaviour>();
        Log.LogInfo($"AmongLLM loaded for {RuntimeInfo.GameVersion} ({RuntimeInfo.PlatformType}).");
    }
}

internal static class RuntimeInfo
{
    public static string PlatformType = "unknown";
    public static string GameVersion = "unknown";
}

public sealed class BridgeBehaviour : MonoBehaviour
{
    private const int MaxSequenceDurationMs = 300_000;
    private const int MaxQueuedCommands = 128;
    private const int MaxFieldCount = 128;
    private const int MaxStringLength = 8192;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ConcurrentQueue<CommandWork> commands = new();
    private readonly ConcurrentQueue<string> logMessages = new();
    private readonly ConcurrentDictionary<string, CommandWork> pending = new();
    private UnityThreadContext? scriptContext;
    private bool commandRunning;
    private int queuedCommands;
    private SocketClient? socketClient;
    private bool originalRunInBackground;
    private float nextHeartbeat;
    private long heartbeatSequence;
    private string? lastScreenId;

    public BridgeBehaviour(IntPtr pointer) : base(pointer) { }

    public void Awake()
    {
        originalRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        socketClient = new SocketClient(EnqueueCommand, message => logMessages.Enqueue(message));
        socketClient.Start();
    }

    public void OnDestroy()
    {
        foreach (var work in pending.Values) work.Cancelled = true;
        Application.runInBackground = originalRunInBackground;
        socketClient?.Stop();
        socketClient = null;
        RuntimeLogs.Shutdown();
    }

    public void Update()
    {
        for (int i = 0; i < 16 && logMessages.TryDequeue(out var message); i++) Debug.Log($"[AmongLLM] {message}");
        scriptContext?.Drain();
        if (Time.realtimeSinceStartup >= nextHeartbeat)
        {
            nextHeartbeat = Time.realtimeSinceStartup + 2f;
            SocketSession? session = socketClient?.CurrentSession;
            object status;
            try { status = GetGameStatus(includeAccountToken: false); }
            catch (Exception error) { status = new { scene = SceneManager.GetActiveScene().name, frame = Time.frameCount, statusError = error.GetType().Name }; }
            session?.TrySend(new
            {
                type = "heartbeat",
                instanceId = SocketClient.InstanceId,
                seq = Interlocked.Increment(ref heartbeatSequence),
                state = status
            });
        }

        if (!commandRunning && commands.TryDequeue(out CommandWork? work))
        {
            Interlocked.Decrement(ref queuedCommands);
            commandRunning = true;
            StartCoroutine(ExecuteWork(work).WrapToIl2Cpp());
        }
    }

    private void EnqueueCommand(JsonElement message, SocketSession session)
    {
        if (!message.TryGetProperty("type", out JsonElement type)) return;
        if (type.GetString() == "cancel")
        {
            if (message.TryGetProperty("id", out var cancelId) && pending.TryGetValue(cancelId.GetString() ?? "", out var cancelled) && ReferenceEquals(cancelled.Session, session)) cancelled.Cancelled = true;
            return;
        }
        if (type.GetString() != "command") return;
        if (!message.TryGetProperty("id", out JsonElement idValue) || string.IsNullOrWhiteSpace(idValue.GetString())) return;
        string id = idValue.GetString()!;
        string operation = message.TryGetProperty("operation", out JsonElement operationValue) ? operationValue.GetString() ?? "" : "";
        JsonElement payload = message.TryGetProperty("payload", out JsonElement payloadValue) ? payloadValue.Clone() : JsonSerializer.SerializeToElement(new { });
        DateTimeOffset? deadline = null;
        if (message.TryGetProperty("deadlineUtc", out JsonElement deadlineValue) &&
            DateTimeOffset.TryParse(deadlineValue.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            deadline = parsed;
        if (Interlocked.Increment(ref queuedCommands) > MaxQueuedCommands)
        {
            Interlocked.Decrement(ref queuedCommands);
            session.TrySend(new { type = "response", id, ok = false, error = new { code = "busy", message = "The game command queue is full." } });
            return;
        }
        var work = new CommandWork(id, operation, payload, deadline, session);
        if (!pending.TryAdd(id, work)) { Interlocked.Decrement(ref queuedCommands); return; }
        commands.Enqueue(work);
    }

    private IEnumerator ExecuteWork(CommandWork work)
    {
        object? result = null;
        string? errorCode = null;
        string? errorMessage = null;
        IEnumerator? operation = null;
        try
        {
        try
        {
            ValidateWork(work);
            operation = BuildOperation(work, value => result = value);
        }
        catch (Exception error)
        {
            errorCode = error.GetType().Name;
            errorMessage = error.Message;
        }

        if (operation != null && errorCode == null)
        {
            while (true)
            {
                bool hasNext;
                object? current = null;
                try
                {
                    ValidateWork(work);
                    hasNext = operation.MoveNext();
                    if (hasNext) current = operation.Current;
                }
                catch (Exception error)
                {
                    errorCode = error.GetType().Name;
                    errorMessage = error.Message;
                    break;
                }
                if (!hasNext) break;
                yield return current;
            }
        }

        if (errorCode == null)
            work.Session.TrySend(new { type = "response", id = work.Id, ok = true, result });
        else
            work.Session.TrySend(new { type = "response", id = work.Id, ok = false, error = new { code = errorCode, message = errorMessage } });
        }
        finally
        {
            try { (operation as IDisposable)?.Dispose(); }
            finally { pending.TryRemove(work.Id, out _); commandRunning = false; }
        }
    }

    private IEnumerator? BuildOperation(CommandWork work, Action<object?> setResult)
    {
        JsonElement p = work.Payload;
        switch (work.Operation)
        {
            case "capture_screenshot":
                return CaptureScreenshot(setResult);
            case "get_game_status":
                setResult(GetGameStatus(includeAccountToken: false));
                return null;
            case "get_screen":
                setResult(GetScreen());
                return null;
            case "click_button":
                setResult(ClickButton(p));
                return null;
            case "click_sequence":
                return RunClickSequence(work, setResult);
            case "set_text":
                setResult(SetText(p));
                return null;
            case "read_game_logs":
                setResult(RuntimeLogs.Read(p));
                return null;
            case "get_account_info":
                setResult(GetAccountInfo(GetBool(p, "includeUserIdToken")));
                return null;
            case "set_itch_auth_token_and_login":
                return SetItchTokenAndLogin(GetString(p, "platformAuthToken"), setResult);
            case "send_chat_message":
                setResult(SendChatMessage(GetString(p, "message")));
                return null;
            case "send_hazel_message":
                setResult(SendHazelMessage(p));
                return null;
            case "send_rpc":
                setResult(SendRpc(p));
                return null;
            case "snap_to":
                setResult(SnapTo(p));
                return null;
            case "list_net_objects":
                setResult(ListNetObjects());
                return null;
            case "read_net_object_properties":
                setResult(ReadNetObjectProperties(p));
                return null;
            case "write_net_object_property":
                setResult(WriteNetObjectProperty(p));
                return null;
            case "execute_csharp":
                return ExecuteCSharp(work, setResult);
            case "list_loaded_mods":
                setResult(ListLoadedMods());
                return null;
            default:
                throw new InvalidOperationException($"Unknown operation '{work.Operation}'.");
        }
    }

    private IEnumerator CaptureScreenshot(Action<object?> setResult)
    {
        yield return new WaitForEndOfFrame();
        Texture2D? texture = null;
        try
        {
            texture = ScreenCapture.CaptureScreenshotAsTexture();
            if (!texture) throw new InvalidOperationException("Unity did not return a screenshot texture.");
            byte[] png = ImageConversion.EncodeToPNG(texture).ToArray();
            setResult(new { mimeType = "image/png", pngBase64 = Convert.ToBase64String(png), width = texture.width, height = texture.height });
        }
        finally
        {
            if (texture) UObject.Destroy(texture);
        }
    }

    private object GetGameStatus(bool includeAccountToken)
    {
        AmongUsClient? client = AmongUsClient.Instance;
        EOSManager? eos = DestroyableSingleton<EOSManager>.InstanceExists ? DestroyableSingleton<EOSManager>.Instance : null;
        var players = UObject.FindObjectsOfType<PlayerControl>()
            .Where(player => player)
            .Select(player => new
            {
                playerId = player.PlayerId,
                netId = player.NetId,
                name = player.Data != null ? player.Data.PlayerName : "",
                isLocal = player == PlayerControl.LocalPlayer,
                dead = player.Data != null && player.Data.IsDead,
                x = player.transform.position.x,
                y = player.transform.position.y
            }).ToArray();
        return new
        {
            gameVersion = RuntimeInfo.GameVersion,
            pluginVersion = Plugin.PluginVersion,
            processId = Environment.ProcessId,
            workingDirectory = Environment.CurrentDirectory,
            platformType = Constants.GetPlatformType().ToString(),
            scene = SceneManager.GetActiveScene().name,
            frame = Time.frameCount,
            isFocused = Application.isFocused,
            isHost = client && client.AmHost,
            connected = client && client.AmConnected,
            gameState = client ? client.GameState.ToString() : "Unavailable",
            gameId = client ? client.GameId : 0,
            eos = GetAccountInfo(includeAccountToken),
            players
        };
    }

    private object GetAccountInfo(bool includeToken)
    {
        bool exists = DestroyableSingleton<EOSManager>.InstanceExists;
        EOSManager? eos = exists ? DestroyableSingleton<EOSManager>.Instance : null;
        string status = DataManager.Player?.Account?.LoginStatus.ToString() ?? "Unavailable";
        bool finished = exists && eos!.HasFinishedLoginFlow();
        bool loggedIn = status == EOSManager.AccountLoginStatus.LoggedIn.ToString();
        var data = new Dictionary<string, object?>
        {
            ["exists"] = exists,
            ["flowFinished"] = finished,
            ["loginStatus"] = status,
            ["loggedIn"] = loggedIn,
            ["ready"] = finished && loggedIn,
            ["platformType"] = Constants.GetPlatformType().ToString(),
            ["puid"] = exists ? ReadAccountField(() => eos!.ProductUserId) : null,
            ["friendCode"] = exists ? ReadAccountField(() => eos!.FriendCode) : null,
            ["tryingToLogin"] = exists && eos!.tryingToLogin
        };
        if (includeToken) data["userIdToken"] = exists ? ReadAccountField(() => eos!.UserIDToken) : null;
        return data;
    }

    private static string? ReadAccountField(Func<string?> read)
    {
        try { return read(); }
        catch { return null; } // Native identity fields may be absent while login initializes.
    }

    private object GetScreen()
    {
        string scene = SceneManager.GetActiveScene().name;
        Camera? camera = Camera.main;
        var buttons = GetActiveButtons().Select(button =>
        {
            Vector3 pixel = camera ? camera!.WorldToScreenPoint(button.transform.position) : Vector3.zero;
            return new
            {
                instanceId = button.GetInstanceID(),
                buttonId = button.GetInstanceID().ToString(CultureInfo.InvariantCulture),
                name = button.name,
                path = GetHierarchy(button.transform),
                labels = button.GetComponentsInChildren<TMP_Text>().Select(text => text.text).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray(),
                x = pixel.x,
                y = Screen.height - pixel.y,
                onUp = button.OnUp,
                onDown = button.OnDown,
                hold = button.HoldToUse,
                repeat = button.OnRepeat,
                clickable = !button.HoldToUse && !button.OnRepeat && (button.OnUp || button.OnDown)
            };
        }).ToArray();
        var textNodes = UObject.FindObjectsOfType<TMP_Text>()
            .Where(text => text && text.gameObject.activeInHierarchy && !text.GetComponentInParent<TMP_InputField>())
            .Select(text => new { name = text.name, path = GetHierarchy(text.transform), text = text.text })
            .Where(item => !string.IsNullOrWhiteSpace(item.text)).Take(300).ToArray();
        var inputs = UObject.FindObjectsOfType<TextBoxTMP>().Where(input => input && input.enabled && input.gameObject.activeInHierarchy)
            .Select(input => new { instanceId = input.GetInstanceID(), name = input.name, path = GetHierarchy(input.transform), kind = "TextBoxTMP", sensitive = input.Hidden, text = input.Hidden ? null : input.text })
            .Concat(UObject.FindObjectsOfType<TMP_InputField>().Where(input => input && input.enabled && input.gameObject.activeInHierarchy)
                .Select(input => new { instanceId = input.GetInstanceID(), name = input.name, path = GetHierarchy(input.transform), kind = "TMP_InputField", sensitive = input.inputType == TMP_InputField.InputType.Password, text = input.inputType == TMP_InputField.InputType.Password ? null : input.text })).ToArray();
        string signature = scene + "|" + string.Join("|", buttons.Select(button => button.instanceId.ToString(CultureInfo.InvariantCulture) + ":" + button.clickable + ":" + string.Join("/", button.labels))) + "|" + string.Join(",", inputs.Select(input => input.instanceId));
        lastScreenId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..16];
        return new { screenId = lastScreenId, scene, frame = Time.frameCount, width = Screen.width, height = Screen.height, buttons, inputs, text = textNodes };
    }

    private object SetText(JsonElement payload)
    {
        var screen = JsonSerializer.SerializeToElement(GetScreen());
        if (GetString(payload, "screenId") != screen.GetProperty("screenId").GetString()) throw new InvalidOperationException("The screen changed; refresh it before setting text.");
        int id = GetInt(payload, "instanceId");
        string text = GetString(payload, "text");
        if (text.Length > MaxStringLength) throw new InvalidOperationException("Text input exceeds 8192 characters.");
        var box = UObject.FindObjectsOfType<TextBoxTMP>().FirstOrDefault(input => input && input.enabled && input.gameObject.activeInHierarchy && input.GetInstanceID() == id);
        if (box)
        {
            box!.SetText(text);
            return new { instanceId = id, length = box.text.Length, sensitive = box.Hidden };
        }
        var tmp = UObject.FindObjectsOfType<TMP_InputField>().FirstOrDefault(input => input && input.enabled && input.gameObject.activeInHierarchy && input.GetInstanceID() == id);
        if (!tmp) throw new InvalidOperationException("The active input control no longer exists.");
        tmp!.text = text;
        return new { instanceId = id, length = tmp.text.Length, sensitive = tmp.inputType == TMP_InputField.InputType.Password };
    }

    private object ClickButton(JsonElement payload)
    {
        JsonElement selector = GetProperty(payload, "selector") ?? payload;
        var suppliedScreen = GetStringOrNull(selector, "screenId") ?? GetStringOrNull(payload, "screenId");
        if (!string.IsNullOrWhiteSpace(suppliedScreen))
        {
            JsonElement snapshot = JsonSerializer.SerializeToElement(GetScreen());
            string actual = snapshot.GetProperty("screenId").GetString()!;
            if (!string.Equals(suppliedScreen, actual, StringComparison.Ordinal)) throw new InvalidOperationException("The screen changed; request a fresh button inventory.");
        }
        PassiveButton target = ResolveButton(selector);
        InvokeButton(target);
        return new { clicked = target.GetInstanceID(), name = target.name, frame = Time.frameCount, screenId = lastScreenId };
    }

    private IEnumerator RunClickSequence(CommandWork work, Action<object?> setResult)
    {
        JsonElement steps = GetProperty(work.Payload, "steps") ?? throw new InvalidOperationException("steps is required.");
        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0 || steps.GetArrayLength() > 64)
            throw new InvalidOperationException("steps must contain 1 to 64 actions.");
        int maxDurationMs = GetIntOr(work.Payload, "timeoutMs", MaxSequenceDurationMs);
        if (maxDurationMs is < 1 or > MaxSequenceDurationMs) throw new InvalidOperationException("timeoutMs must be between 1 and 300000.");
        float deadline = Time.realtimeSinceStartup + maxDurationMs / 1000f;
        int completed = 0;
        foreach (JsonElement step in steps.EnumerateArray())
        {
            if (Time.realtimeSinceStartup >= deadline) throw new TimeoutException("Click sequence exceeded its 5 minute limit.");
            string kind = GetStringOr(step, "kind", GetStringOr(step, "type", ""));
            if (kind == "wait")
            {
                int milliseconds = GetInt(step, "milliseconds");
                if (milliseconds < 0 || milliseconds > 60_000) throw new InvalidOperationException("wait milliseconds must be between 0 and 60000.");
                float waitUntil = Math.Min(deadline, Time.realtimeSinceStartup + milliseconds / 1000f);
                while (Time.realtimeSinceStartup < waitUntil) yield return null;
                if (Time.realtimeSinceStartup >= deadline) throw new TimeoutException("Click sequence exceeded its overall timeout.");
            }
            else if (kind == "waitForButton")
            {
                JsonElement selector = GetProperty(step, "selector") ?? throw new InvalidOperationException("waitForButton selector is required.");
                int timeoutMs = GetInt(step, "timeoutMs");
                if (timeoutMs <= 0 || timeoutMs > 60_000) throw new InvalidOperationException("waitForButton timeoutMs must be between 1 and 60000.");
                float buttonDeadline = Math.Min(deadline, Time.realtimeSinceStartup + timeoutMs / 1000f);
                bool found = false;
                while (Time.realtimeSinceStartup < buttonDeadline)
                {
                    if (TryResolveButton(selector, out _)) { found = true; break; }
                    yield return null;
                }
                if (!found) throw new TimeoutException("The selected button did not appear before timeout.");
            }
            else if (kind == "click")
            {
                JsonElement selector = GetProperty(step, "selector") ?? throw new InvalidOperationException("click selector is required.");
                ClickButton(step);
            }
            else
            {
                throw new InvalidOperationException($"Unsupported click_sequence step '{kind}'.");
            }
            completed++;
            work.Session.TrySend(new { type = "progress", id = work.Id, progress = completed, total = steps.GetArrayLength(), message = $"Completed step {completed}." });
            yield return null;
        }
        setResult(new { completedSteps = completed, totalSteps = steps.GetArrayLength(), frame = Time.frameCount });
    }

    private IEnumerator SetItchTokenAndLogin(string token, Action<object?> setResult)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16384) throw new InvalidOperationException("authToken is required and must be at most 16384 characters.");
        if (Constants.GetPlatformType() != Platforms.StandaloneItch) throw new InvalidOperationException("Account switching is available only when the runtime platform is Itch.");
        AmongUsClient? client = AmongUsClient.Instance;
        if (client && client.AmConnected) throw new InvalidOperationException("Leave the current room before switching the EOS platform account.");
        EOSManager eos = DestroyableSingleton<EOSManager>.Instance;
        if (eos.tryingToLogin) throw new InvalidOperationException("An EOS login is already in progress. Wait for it to finish before switching accounts.");
        SetPlatformAuthToken(eos, token);
        eos.LoginWithCorrectPlatform();
        float deadline = Time.realtimeSinceStartup + 90f;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (eos.HasFinishedLoginFlow() && DataManager.Player.Account.LoginStatus == EOSManager.AccountLoginStatus.LoggedIn)
            {
                setResult(new { success = true, account = GetAccountInfo(includeToken: false) });
                yield break;
            }
            yield return null;
        }
        throw new TimeoutException("LoginWithCorrectPlatform did not reach HasFinishedLoginFlow and LoggedIn within 90 seconds.");
    }

    private static void SetPlatformAuthToken(EOSManager eos, string token)
    {
        if (eos.Pointer == IntPtr.Zero) throw new InvalidOperationException("EOSManager native instance pointer is null.");
        // Audited installed interop setter: native field offset + ManagedStringToIl2Cpp + FieldWriteWbarrierStub.
        // The original private field is emitted as a public interop field-access property.
        eos.platformAuthToken = token;
        if (!string.Equals(eos.platformAuthToken, token, StringComparison.Ordinal)) throw new InvalidOperationException("The Itch platformAuthToken field did not match after native write.");
    }

    private object SendChatMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxStringLength) throw new InvalidOperationException("text must contain 1 to 8192 characters.");
        PlayerControl local = PlayerControl.LocalPlayer;
        if (!local) throw new InvalidOperationException("There is no local player to send chat.");
        return new { accepted = local.RpcSendChat(text) };
    }

    private object SendHazelMessage(JsonElement payload)
    {
        string mode = GetStringOr(payload, "mode", "raw");
        if (mode == "rpc") return SendRpc(payload);
        if (mode != "raw") throw new InvalidOperationException("mode must be raw or rpc.");
        AmongUsClient client = AmongUsClient.Instance;
        if (!client || !client.AmConnected) throw new InvalidOperationException("A connected game client is required.");
        JsonElement fields = GetProperty(payload, "fields") ?? throw new InvalidOperationException("fields is required.");
        MessageWriter writer = MessageWriter.Get(ParseSendOption(payload));
        try
        {
            WriteHazelFields(writer, fields);
            if (writer.Length > 60_000) throw new InvalidOperationException("Hazel message exceeds 60000 bytes.");
            if (client.connection == null) throw new InvalidOperationException("The game client has no transport connection.");
            SendErrors error = client.connection.Send(writer);
            if (error != SendErrors.None) throw new IOException($"Hazel transport rejected the message: {error}.");
            return new { submitted = true, mode, bytes = writer.Length, sendError = error.ToString() };
        }
        finally { writer.Recycle(); }
    }

    private static SendOption ParseSendOption(JsonElement payload) => GetStringOr(payload, "sendOption", "Reliable") switch
    {
        "Reliable" => SendOption.Reliable,
        "Unreliable" => SendOption.None,
        _ => throw new InvalidOperationException("sendOption must be Reliable or Unreliable.")
    };

    private object SendRpc(JsonElement payload)
    {
        AmongUsClient client = AmongUsClient.Instance;
        if (!client || !client.AmConnected) throw new InvalidOperationException("A connected game client is required.");
        uint netId = GetUInt(payload, "targetNetId");
        byte callId = checked((byte)GetInt(payload, "callId"));
        int targetClientId = GetIntOr(payload, "targetClientId", -1);
        SendOption option = ParseSendOption(payload);
        InnerNetObject? target = client.FindObjectByNetId<InnerNetObject>(netId);
        if (!target) throw new InvalidOperationException($"No room net object exists with netId {netId}.");
        if (!client.AmHost && !target!.AmOwner) throw new InvalidOperationException("Non-host clients can send RPCs only for objects they own.");
        JsonElement fields = GetProperty(payload, "fields") ?? throw new InvalidOperationException("fields is required.");
        if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() > MaxFieldCount) throw new InvalidOperationException("fields must be an array with at most 128 entries.");

        MessageWriter writer = client.StartRpcImmediately(netId, callId, option, targetClientId);
        bool sent = false;
        try
        {
            WriteHazelFields(writer, fields);
            if (writer.Length > 60_000) throw new InvalidOperationException("Hazel message exceeds 60000 bytes.");
            client.FinishRpcImmediately(writer);
            sent = true;
        }
        finally
        {
            if (!sent) writer.Recycle();
        }
        return new { submitted = true, netId, callId, targetClientId, fieldCount = fields.GetArrayLength(), transportAcknowledgement = false };
    }

    private static void WriteHazelFields(MessageWriter writer, JsonElement fields)
    {
        if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() > MaxFieldCount) throw new InvalidOperationException("fields must contain at most 128 entries.");
        int depth = 0;
        foreach (JsonElement field in fields.EnumerateArray())
        {
            string type = GetString(field, "type");
            if (type == "start")
            {
                if (++depth > 16) throw new InvalidOperationException("Hazel nesting exceeds 16 tags.");
                writer.StartMessage(checked((byte)GetInt(field, "tag")));
            }
            else if (type == "end")
            {
                if (--depth < 0) throw new InvalidOperationException("Hazel end tag has no matching start.");
                writer.EndMessage();
            }
            else WriteHazelField(writer, field);
            if (writer.Length > 60_000) throw new InvalidOperationException("Hazel message exceeds 60000 bytes.");
        }
        if (depth != 0) throw new InvalidOperationException("Hazel message contains unclosed tags.");
    }

    private static void WriteHazelField(MessageWriter writer, JsonElement field)
    {
        string type = GetString(field, "type");
        JsonElement value = GetProperty(field, "value") ?? throw new InvalidOperationException("Each Hazel field requires a value.");
        switch (type)
        {
            case "bool": writer.Write(value.GetBoolean()); break;
            case "byte": writer.Write(checked((byte)value.GetInt32())); break;
            case "sbyte": writer.Write(checked((sbyte)value.GetInt32())); break;
            case "int16": writer.Write(unchecked((ushort)checked((short)value.GetInt32()))); break;
            case "uint16": writer.Write(checked((ushort)value.GetInt32())); break;
            case "int32": writer.Write(value.GetInt32()); break;
            case "uint32": writer.Write(value.GetUInt32()); break;
            case "int64": writer.Write(unchecked((ulong)value.GetInt64())); break;
            case "uint64": writer.Write(value.GetUInt64()); break;
            case "packedInt32": writer.WritePacked(value.GetInt32()); break;
            case "packedUInt32": writer.WritePacked(value.GetUInt32()); break;
            case "float":
                float number = value.GetSingle();
                if (!float.IsFinite(number)) throw new InvalidOperationException("Hazel float values must be finite.");
                writer.Write(number);
                break;
            case "string":
                string text = value.GetString() ?? "";
                if (text.Length > MaxStringLength) throw new InvalidOperationException("Hazel string exceeds the configured limit.");
                writer.Write(text);
                break;
            case "vector2":
                float x = GetSingle(value, "x");
                float y = GetSingle(value, "y");
                if (!float.IsFinite(x) || !float.IsFinite(y)) throw new InvalidOperationException("Vector2 values must be finite.");
                NetHelpers.WriteVector2(new Vector2(x, y), writer);
                break;
            case "bytes":
                byte[] bytes = Convert.FromBase64String(value.GetString() ?? "");
                if (bytes.Length > 65536) throw new InvalidOperationException("Hazel byte field exceeds 65536 bytes.");
                foreach (byte b in bytes) writer.Write(b);
                break;
            default: throw new InvalidOperationException($"Unsupported Hazel field type '{type}'.");
        }
    }

    private object SnapTo(JsonElement payload)
    {
        AmongUsClient client = AmongUsClient.Instance;
        if (!client || !client.AmConnected) throw new InvalidOperationException("A connected game client is required.");
        PlayerControl[] players = UObject.FindObjectsOfType<PlayerControl>().Where(player => player).ToArray();
        PlayerControl? target = null;
        if (payload.TryGetProperty("netObjectId", out JsonElement objectId))
            target = players.FirstOrDefault(player => player.NetId == objectId.GetUInt32());
        else if (payload.TryGetProperty("playerId", out JsonElement playerId))
            target = players.FirstOrDefault(player => player.PlayerId == playerId.GetByte());
        else if (payload.TryGetProperty("netId", out JsonElement netId))
            target = players.FirstOrDefault(player => player.NetId == netId.GetUInt32());
        else
            throw new InvalidOperationException("Specify playerId or netId.");
        if (!target) throw new InvalidOperationException("The requested player is not present in the current room.");
        if (!client.AmHost && target != PlayerControl.LocalPlayer) throw new InvalidOperationException("Only the room host may snap another player's transform.");
        JsonElement position = GetProperty(payload, "position") ?? payload;
        float x = GetSingle(position, "x");
        float y = GetSingle(position, "y");
        if (!float.IsFinite(x) || !float.IsFinite(y) || Math.Abs(x) > 1000 || Math.Abs(y) > 1000)
            throw new InvalidOperationException("Position must be finite and within +/-1000 game units.");
        if (!target!.NetTransform) throw new InvalidOperationException("The requested player has no network transform.");
        target.NetTransform.RpcSnapTo(new Vector2(x, y));
        return new { accepted = true, playerId = target.PlayerId, netId = target.NetId, x, y, host = client.AmHost };
    }

    private object ListNetObjects()
    {
        AmongUsClient? client = AmongUsClient.Instance;
        bool isHost = client && client.AmHost;
        var items = SnapshotNetObjects()
            .Select(item => new { netId = item.NetId, type = item.GetIl2CppType().FullName, name = item.name, ownerId = item.OwnerId, owned = item.AmOwner, active = item.gameObject.activeInHierarchy, instanceId = item.GetInstanceID() })
            .OrderBy(item => item.netId).ToArray();
        return new { host = isHost, count = items.Length, objects = items };
    }

    private object ReadNetObjectProperties(JsonElement payload)
    {
        InnerNetObject target = FindNetObject(GetUInt(payload, "netObjectId"));
        EnsureObjectAccess(target, write: false);
        JsonElement? memberNames = GetProperty(payload, "members");
        var metadata = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Select(property => new { name = property.Name, type = property.PropertyType.FullName, readable = property.GetMethod?.IsPublic == true, writable = property.SetMethod?.IsPublic == true }).ToArray();
        if (memberNames is null || memberNames.Value.ValueKind == JsonValueKind.Null)
            return new { netId = target.NetId, type = target.GetType().FullName, members = metadata };
        if (memberNames.Value.ValueKind != JsonValueKind.Array || memberNames.Value.GetArrayLength() > 128)
            throw new InvalidOperationException("members must be an array of up to 128 property paths.");
        var values = new Dictionary<string, object?>();
        foreach (JsonElement item in memberNames.Value.EnumerateArray())
        {
            string path = item.GetString() ?? throw new InvalidOperationException("Member paths must be strings.");
            object? value = ResolveMemberValue(target, path);
            values[path] = DescribeValue(value, 0);
        }
        return new { netId = target.NetId, type = target.GetType().FullName, properties = values };
    }

    private object WriteNetObjectProperty(JsonElement payload)
    {
        InnerNetObject target = FindNetObject(GetUInt(payload, "netObjectId"));
        EnsureObjectAccess(target, write: true);
        string path = GetString(payload, "member");
        JsonElement value = GetProperty(payload, "value") ?? throw new InvalidOperationException("value is required.");
        SetMemberValue(target, path, value);
        object? readBack = ResolveMemberValue(target, path);
        return new { netId = target.NetId, type = target.GetType().FullName, memberPath = path, value = DescribeValue(readBack, 0), host = AmongUsClient.Instance.AmHost };
    }

    private static InnerNetObject FindNetObject(uint netId)
    {
        AmongUsClient client = AmongUsClient.Instance;
        InnerNetObject? target = client ? client.FindObjectByNetId<InnerNetObject>(netId) : null;
        if (!target) throw new InvalidOperationException($"No room net object exists with netId {netId}.");
        string nativeName = target!.GetIl2CppType().FullName;
        Type? wrapperType = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(nativeName, false)).FirstOrDefault(type => type != null && typeof(InnerNetObject).IsAssignableFrom(type));
        return wrapperType != null ? (InnerNetObject)(Activator.CreateInstance(wrapperType, target.Pointer) ?? target) : target;
    }

    private static void EnsureObjectAccess(InnerNetObject target, bool write)
    {
        AmongUsClient? client = AmongUsClient.Instance;
        if (!client || !client.AmConnected) throw new InvalidOperationException("A connected game client is required.");
        if (!write || client.AmHost || target.AmOwner) return;
        throw new InvalidOperationException("Only the room host may modify objects owned by others.");
    }

    private static InnerNetObject[] SnapshotNetObjects()
    {
        AmongUsClient client = AmongUsClient.Instance;
        if (!client || !client.AmConnected) throw new InvalidOperationException("A connected game client is required.");
        var collection = client.allObjects;
        IL2CPP.il2cpp_monitor_enter(collection.Pointer);
        try
        {
            var items = new List<InnerNetObject>();
            var list = collection.allObjects;
            for (int i = 0; i < list.Count; i++) if (list[i]) items.Add(list[i]);
            return items.ToArray();
        }
        finally { IL2CPP.il2cpp_monitor_exit(collection.Pointer); }
    }

    private static object? ResolveMemberValue(object root, string path) => PropertyPath.Read(root, path);

    private static void SetMemberValue(object root, string path, JsonElement value) => PropertyPath.Write(root, path, type => ConvertJsonValue(value, type));

    private static object? ConvertJsonValue(JsonElement value, Type targetType)
    {
        Type effective = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (effective.IsEnum) return Enum.Parse(effective, value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetInt32().ToString(CultureInfo.InvariantCulture), ignoreCase: true);
        if (effective == typeof(string)) return value.GetString();
        if (effective == typeof(bool)) return value.GetBoolean();
        if (effective == typeof(byte)) return checked((byte)value.GetInt32());
        if (effective == typeof(sbyte)) return checked((sbyte)value.GetInt32());
        if (effective == typeof(short)) return checked((short)value.GetInt32());
        if (effective == typeof(ushort)) return checked((ushort)value.GetInt32());
        if (effective == typeof(int)) return value.GetInt32();
        if (effective == typeof(uint)) return value.GetUInt32();
        if (effective == typeof(long)) return value.GetInt64();
        if (effective == typeof(ulong)) return value.GetUInt64();
        if (effective == typeof(float)) return value.GetSingle();
        if (effective == typeof(double)) return value.GetDouble();
        if (effective == typeof(Vector2)) return new Vector2(GetSingle(value, "x"), GetSingle(value, "y"));
        if (effective == typeof(Vector3)) return new Vector3(GetSingle(value, "x"), GetSingle(value, "y"), GetSingle(value, "z"));
        if (effective == typeof(Color)) return new Color(GetSingle(value, "r"), GetSingle(value, "g"), GetSingle(value, "b"), GetSingleOr(value, "a", 1f));
        throw new InvalidOperationException($"Writes to {effective.FullName} are not supported by the structured property writer.");
    }

    private static object? DescribeValue(object? value, int depth)
    {
        if (value == null) return null;
        if (depth > 4) return new { type = value.GetType().FullName, truncated = true };
        if (value is string || value.GetType().IsPrimitive || value is decimal) return value;
        if (value.GetType().IsEnum) return new { type = value.GetType().FullName, name = value.ToString(), value = Convert.ToInt64(value, CultureInfo.InvariantCulture) };
        if (value is Vector2 v2) return new { x = v2.x, y = v2.y };
        if (value is Vector3 v3) return new { x = v3.x, y = v3.y, z = v3.z };
        if (value is Color color) return new { r = color.r, g = color.g, b = color.b, a = color.a };
        if (value is InnerNetObject netObject) return new { type = value.GetType().FullName, netId = netObject.NetId, name = netObject.name };
        if (value is UObject unityObject) return unityObject ? new { type = value.GetType().FullName, name = unityObject.name, instanceId = unityObject.GetInstanceID() } : null;
        if (value is System.Collections.IEnumerable enumerable)
        {
            var items = new List<object?>();
            foreach (object? item in enumerable)
            {
                if (items.Count >= 64) { items.Add(new { truncated = true }); break; }
                items.Add(DescribeValue(item, depth + 1));
            }
            return items;
        }
        return new { type = value.GetType().FullName, value = value.ToString() };
    }

    private IEnumerator ExecuteCSharp(CommandWork work, Action<object?> setResult)
    {
        string code = GetString(work.Payload, "code");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 32000) throw new InvalidOperationException("code must contain 1 to 32000 characters.");
        // Compilation does not access Unity and can run off-thread. Invoke the compiled script on Unity's thread.
        var compilation = Task.Run(() =>
        {
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && !string.IsNullOrWhiteSpace(assembly.Location))
                .Select(assembly => (MetadataReference)MetadataReference.CreateFromFile(assembly.Location))
                .DistinctBy(reference => reference.Display, StringComparer.OrdinalIgnoreCase);
            ScriptOptions options = ScriptOptions.Default.WithReferences(references)
                .WithImports("System", "System.Linq", "System.Threading.Tasks", "UnityEngine", "InnerNet");
            return CSharpScript.Create<object?>(code, options, typeof(ScriptGlobals)).CreateDelegate();
        });
        while (!compilation.IsCompleted) yield return null;
        ScriptRunner<object?> runner = compilation.GetAwaiter().GetResult();
        using var context = new UnityThreadContext(() => IsWorkCancelled(work));
        scriptContext = context;
        try
        {
            Task<object?> execution = context.Run(() => runner(new ScriptGlobals(this)));
            while (!execution.IsCompleted) yield return null;
            object? value = execution.GetAwaiter().GetResult();
            setResult(new { result = DescribeValue(value, 0), warning = "Full in-process privileges. Cancellation is cooperative; synchronous loops cannot be interrupted safely." });
        }
        finally { scriptContext = null; }
    }

    private object ListLoadedMods()
    {
        var plugins = IL2CPPChainloader.Instance.Plugins.Values.Select(info => new
        {
            guid = info.Metadata.GUID,
            name = info.Metadata.Name,
            version = info.Metadata.Version.ToString(),
            location = Path.GetFileName(info.Location)
        }).OrderBy(info => info.guid, StringComparer.OrdinalIgnoreCase).ToArray();
        return new { count = plugins.Length, plugins };
    }

    private static PassiveButton ResolveButton(JsonElement selector)
    {
        if (!TryResolveButton(selector, out PassiveButton? button)) throw new InvalidOperationException("No active button matches the selector.");
        return button!;
    }

    private static bool TryResolveButton(JsonElement selector, out PassiveButton? button)
    {
        if (selector.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("A button selector must be an object.");
        int? instanceId = selector.TryGetProperty("instanceId", out JsonElement id) && id.TryGetInt32(out int parsedId) ? parsedId : null;
        if (selector.TryGetProperty("buttonId", out var buttonId) && int.TryParse(buttonId.ToString(), out var numericId)) instanceId = numericId;
        string? name = GetStringOrNull(selector, "name");
        string? path = GetStringOrNull(selector, "path");
        string? label = GetStringOrNull(selector, "labelContains");
        string? exactLabel = GetStringOrNull(selector, "label");
        string? scene = GetStringOrNull(selector, "scene");
        if (!string.IsNullOrEmpty(scene) && scene != SceneManager.GetActiveScene().name) { button = null; return false; }
        PassiveButton[] matches = GetActiveButtons().Where(candidate => !candidate.HoldToUse && !candidate.OnRepeat && (candidate.OnUp || candidate.OnDown)).Where(candidate =>
        {
            if (instanceId.HasValue && candidate.GetInstanceID() != instanceId.Value) return false;
            if (name != null && !string.Equals(candidate.name, name, StringComparison.Ordinal)) return false;
            if (path != null && !string.Equals(GetHierarchy(candidate.transform), path, StringComparison.Ordinal)) return false;
            if (label != null && !candidate.GetComponentsInChildren<TMP_Text>().Any(text => text.text.Contains(label, StringComparison.OrdinalIgnoreCase))) return false;
            if (exactLabel != null && !candidate.GetComponentsInChildren<TMP_Text>().Any(text => string.Equals(text.text, exactLabel, StringComparison.OrdinalIgnoreCase))) return false;
            return instanceId.HasValue || name != null || path != null || label != null || exactLabel != null;
        }).Take(2).ToArray();
        if (matches.Length != 1) { button = null; return false; }
        button = matches[0];
        return true;
    }

    private static void InvokeButton(PassiveButton button)
    {
        if (!button || !button.enabled || !button.gameObject.activeInHierarchy) throw new InvalidOperationException("Button is no longer active.");
        if (button.HoldToUse || button.OnRepeat) throw new InvalidOperationException("Hold/repeat buttons require timed input and are not supported by click operations.");
        if (button.OnUp) button.ReceiveClickUp();
        else if (button.OnDown) button.ReceiveClickDown();
        else throw new InvalidOperationException("Button does not expose a click-up or click-down event.");
    }

    private static IEnumerable<PassiveButton> GetActiveButtons() => UObject.FindObjectsOfType<PassiveButton>().Where(button => button && button.enabled && button.gameObject.activeInHierarchy);

    private static string GetHierarchy(Transform transform)
    {
        var parts = new List<string>();
        for (Transform? current = transform; current && parts.Count < 30; current = current!.parent) parts.Add(current!.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static JsonElement? GetProperty(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) ? value : null;
    private static string GetString(JsonElement element, string name) => GetProperty(element, name)?.GetString() ?? throw new InvalidOperationException($"{name} is required.");
    private static string GetStringOr(JsonElement element, string name, string fallback) => GetProperty(element, name)?.GetString() ?? fallback;
    private static string? GetStringOrNull(JsonElement element, string name) => GetProperty(element, name)?.GetString();
    private static bool GetBool(JsonElement element, string name) => GetProperty(element, name)?.GetBoolean() ?? false;
    private static int GetInt(JsonElement element, string name) => GetProperty(element, name)?.GetInt32() ?? throw new InvalidOperationException($"{name} is required.");
    private static int GetIntOr(JsonElement element, string name, int fallback) => GetProperty(element, name)?.GetInt32() ?? fallback;
    private static uint GetUInt(JsonElement element, string name) => GetProperty(element, name)?.GetUInt32() ?? throw new InvalidOperationException($"{name} is required.");
    private static float GetSingle(JsonElement element, string name) => GetProperty(element, name)?.GetSingle() ?? throw new InvalidOperationException($"{name} is required.");
    private static float GetSingleOr(JsonElement element, string name, float fallback) => GetProperty(element, name)?.GetSingle() ?? fallback;

    private static bool IsWorkCancelled(CommandWork work) => work.Cancelled || !work.Session.IsActive || work.DeadlineUtc <= DateTimeOffset.UtcNow;

    private static void ValidateWork(CommandWork work)
    {
        if (IsWorkCancelled(work)) throw new OperationCanceledException("Command was cancelled, disconnected, or its deadline elapsed.");
    }

    private sealed record CommandWork(string Id, string Operation, JsonElement Payload, DateTimeOffset? DeadlineUtc, SocketSession Session)
    {
        public volatile bool Cancelled;
    }
    public sealed class ScriptGlobals
    {
        public BridgeBehaviour Plugin { get; }
        internal ScriptGlobals(BridgeBehaviour plugin) => Plugin = plugin;
    }
}
