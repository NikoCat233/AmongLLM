using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepLogger = BepInEx.Logging.Logger;
using UnityEngine;

namespace AmongLLM.GamePlugin;

/// <summary>
/// Mirrors every live BepInEx log source. Listener callbacks only format and enqueue
/// records; the JSONL mirror is written by GameLogMirrorWriter's worker thread.
/// </summary>
internal sealed class GameLogs : ILogListener, IDisposable
{
    private static readonly object UnitySourceGate = new();
    private readonly object publishGate = new();
    private readonly GameLogBuffer buffer;
    private readonly GameLogMirrorWriter mirror;
    private readonly DateTimeOffset captureStartedAtUtc = DateTimeOffset.UtcNow;
    private Application.LogCallback? unityCallback;
    private int installed;
    private int disposed;

    public string MirrorPath => mirror.Path;
    public string? BootstrapLogPath { get; }
    public LogLevel LogLevelFilter => LogLevel.All;

    public GameLogs(string instanceId, int memoryCapacity = 4096)
    {
        buffer = new GameLogBuffer(memoryCapacity);
        string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
        string safeId = string.Concat((instanceId ?? "").Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeId)) safeId = Guid.NewGuid().ToString("N");
        string mirrorPath = Path.Combine(localAppData, "AmongLLM", "logs", $"{safeId}.jsonl");
        mirror = new GameLogMirrorWriter(mirrorPath);
        try { BootstrapLogPath = Path.Combine(Paths.BepInExRootPath, "LogOutput.log"); }
        catch { BootstrapLogPath = null; }
    }

    public void Install()
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref installed, 1) != 0) return;
        try { BepLogger.Listeners.Add(this); }
        catch (Exception error) { RecordInternalFailure("BepInEx listener registration", error); }
        EnsureUnitySource();
    }

    /// <summary>
    /// Ensures Unity's logs arrive through BepInEx exactly once. Current BepInEx IL2CPP
    /// provides IL2CPPUnityLogSource; when absent, subscribe to Unity's additive event
    /// without replacing the game's existing callbacks.
    /// </summary>
    public void EnsureUnitySource()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        lock (UnitySourceGate)
        {
            try
            {
                if (BepLogger.Sources.Any(IsUnitySource)) return;
            }
            catch (Exception error)
            {
                RecordInternalFailure("Unity log source discovery", error);
            }

            if (unityCallback is not null) return;
            try
            {
                unityCallback = (Action<string, string, LogType>)OnUnityLog;
                Application.add_logMessageReceivedThreaded(unityCallback);
            }
            catch (Exception error)
            {
                unityCallback = null;
                RecordInternalFailure("Unity log callback registration", error);
            }
        }
    }

    private void OnUnityLog(string message, string stackTrace, LogType type)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        // A Unity source can appear after this mod loads; it then owns the mirrored entry.
        try { if (BepLogger.Sources.Any(IsUnitySource)) return; } catch { }
        string level = type switch { LogType.Error or LogType.Exception or LogType.Assert => "Error", LogType.Warning => "Warning", _ => "Info" };
        Publish("Unity", level, message, stackTrace);
    }

    private static bool IsUnitySource(ILogSource source)
    {
        string? typeName = source.GetType().FullName;
        return string.Equals(typeName, "BepInEx.Unity.IL2CPP.Logging.IL2CPPUnityLogSource", StringComparison.Ordinal) ||
               typeName?.Contains("UnityLogSource", StringComparison.OrdinalIgnoreCase) == true ||
               string.Equals(source.SourceName, "Unity", StringComparison.OrdinalIgnoreCase);
    }

    public void LogEvent(object sender, LogEventArgs args)
    {
        try
        {
            string source = args.Source?.SourceName ?? "unknown";
            string data;
            try { data = args.Data?.ToString() ?? ""; }
            catch (Exception error) { data = $"[Log data ToString failed: {error.GetType().Name}]"; }
            (string message, string? stackTrace) = SplitMessageAndStackTrace(data);
            Publish(source, args.Level.ToString(), message, stackTrace);
        }
        catch (Exception error)
        {
            RecordInternalFailure("BepInEx log listener", error);
        }
    }

    public GameLogsReadResponse Read(JsonElement payload)
    {
        long after = TryReadInt64(payload, "afterSequence") ?? 0;
        int limit = (int)Math.Clamp(TryReadInt64(payload, "limit") ?? 200, 1, 1000);
        GameLogReadPage page = buffer.ReadAfter(after, limit, ReadStrings(payload, "sources"), ReadStrings(payload, "levels"));
        return new GameLogsReadResponse(page.Entries, page.NextSequence, page.OldestSequence, page.Truncated,
            page.DroppedMemoryEntries, mirror.DroppedEntries, mirror.DiskFailureCount, page.SourceCounts,
            mirror.Path, mirror.LastError, BootstrapLogPath, captureStartedAtUtc);
    }

    private void Publish(string source, string level, string message, string? stackTrace)
    {
        lock (publishGate)
        {
            GameLogEntry entry = buffer.Append(source, level, message, stackTrace);
            mirror.TryWrite(entry);
        }
    }

    private static (string Message, string? StackTrace) SplitMessageAndStackTrace(string text)
    {
        if (string.IsNullOrEmpty(text)) return ("", null);
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        int stackStart = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimStart();
            if (line.StartsWith("at ", StringComparison.Ordinal) ||
                line.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                line.StartsWith("Stack trace:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Rethrow as ", StringComparison.Ordinal))
            {
                stackStart = i;
                break;
            }
        }
        if (stackStart < 0) return (text, null);
        return (string.Join(Environment.NewLine, lines.Take(stackStart)).TrimEnd(),
            string.Join(Environment.NewLine, lines.Skip(stackStart)).TrimEnd());
    }

    private static long? TryReadInt64(JsonElement payload, string property)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
        return null;
    }

    private static string? TryReadString(JsonElement payload, string property) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[]? ReadStrings(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(value => value.GetString() ?? "").Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
            : null;

    private void RecordInternalFailure(string operation, Exception error)
    {
        // Never report failures through BepInEx logging again; that would recurse.
        try { Publish("AmongLLM.GameLogs", "Error", $"{operation} failed: {error.GetType().Name}: {error.Message}", error.StackTrace); }
        catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (Interlocked.Exchange(ref installed, 0) != 0)
        {
            try { BepLogger.Listeners.Remove(this); } catch { }
        }

        lock (UnitySourceGate)
        {
            if (unityCallback is not null)
            {
                try { Application.remove_logMessageReceivedThreaded(unityCallback); } catch { }
                unityCallback = null;
            }
        }
        mirror.Dispose();
    }
}

internal static class RuntimeLogs
{
    private static GameLogs? current;
    public static void Install()
    {
        current ??= new GameLogs(SocketClient.InstanceId);
        current.Install();
    }
    public static GameLogsReadResponse Read(JsonElement payload) => current?.Read(payload) ?? throw new InvalidOperationException("Game log mirror has not initialized.");
    public static void Shutdown() => Interlocked.Exchange(ref current, null)?.Dispose();
}
