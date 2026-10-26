using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AmongLLM.GamePlugin;

internal sealed record GameLogEntry(
    long Seq,
    DateTimeOffset Time,
    string Source,
    string Level,
    string Message,
    string? StackTrace);

internal sealed record GameLogReadPage(
    IReadOnlyList<GameLogEntry> Entries,
    long NextSequence,
    long OldestSequence,
    bool Truncated,
    long DroppedMemoryEntries,
    IReadOnlyDictionary<string, long> SourceCounts);

/// <summary>
/// Thread-safe, Unity-free cursor buffer. Cursor values are inclusive sequence numbers;
/// a page advances to one past its last returned record.
/// </summary>
internal sealed class GameLogBuffer
{
    private readonly object gate = new();
    private readonly Queue<GameLogEntry> entries;
    private readonly Dictionary<string, long> sourceCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly int capacity;
    private long nextSequence = 1;
    private long droppedMemoryEntries;

    public GameLogBuffer(int capacity = 4096)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        entries = new Queue<GameLogEntry>(capacity);
    }

    public GameLogEntry Append(string source, string level, string message, string? stackTrace = null, DateTimeOffset? time = null)
    {
        lock (gate)
        {
            source = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
            level = string.IsNullOrWhiteSpace(level) ? "Unknown" : level;
            var entry = new GameLogEntry(nextSequence++, time ?? DateTimeOffset.UtcNow, source, level, message ?? "", stackTrace);
            if (entries.Count == capacity)
            {
                entries.Dequeue();
                droppedMemoryEntries++;
            }
            entries.Enqueue(entry);
            sourceCounts[source] = sourceCounts.TryGetValue(source, out long count) ? count + 1 : 1;
            return entry;
        }
    }

    public GameLogReadPage ReadAfter(long afterSequence, int limit = 200, string[]? sources = null, string[]? levels = null)
    {
        if (afterSequence < 0 || afterSequence == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var page = Read(afterSequence + 1, limit, sources: sources, levels: levels);
        // Public MCP cursors are exclusive: passing nextSequence as afterSequence never repeats a record.
        return page with { NextSequence = page.NextSequence - 1 };
    }

    public GameLogReadPage Read(long? cursor = null, int limit = 100, string? source = null, string? level = null, string[]? sources = null, string[]? levels = null)
    {
        limit = Math.Clamp(limit, 1, 1000);
        source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        level = string.IsNullOrWhiteSpace(level) ? null : level.Trim();
        var sourceFilter = sources is { Length: > 0 } ? new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase) : null;
        var levelFilter = levels is { Length: > 0 } ? new HashSet<string>(levels, StringComparer.OrdinalIgnoreCase) : null;

        lock (gate)
        {
            GameLogEntry[] snapshot = entries.ToArray();
            long oldestSequence = snapshot.Length == 0 ? nextSequence : snapshot[0].Seq;
            long requested = cursor ?? oldestSequence;
            bool truncated = cursor.HasValue && requested < oldestSequence;
            long firstSequence = Math.Max(oldestSequence, requested);
            if (firstSequence > nextSequence) firstSequence = nextSequence;

            var page = new List<GameLogEntry>(Math.Min(limit, snapshot.Length));
            foreach (GameLogEntry entry in snapshot)
            {
                if (entry.Seq < firstSequence) continue;
                if (source is not null && !string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase)) continue;
                if (level is not null && !string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase)) continue;
                if (sourceFilter != null && !sourceFilter.Contains(entry.Source)) continue;
                if (levelFilter != null && !levelFilter.Contains(entry.Level)) continue;
                page.Add(entry);
                if (page.Count == limit) break;
            }

            // When the page fills, resume after its last result. Entries skipped by a
            // filter are reconsidered on the next page, so filtered sequence gaps remain safe.
            long next = page.Count == limit ? page[^1].Seq + 1 : nextSequence;
            var counts = new SortedDictionary<string, long>(sourceCounts, StringComparer.OrdinalIgnoreCase);
            return new GameLogReadPage(page, next, oldestSequence, truncated, droppedMemoryEntries, counts);
        }
    }
}

/// <summary>
/// Background JSONL writer. Producers do no disk I/O; queue overflow and disk failures
/// both have explicit counters and a last error for diagnostics.
/// </summary>
internal sealed class GameLogMirrorWriter : IDisposable
{
    private const int QueueCapacity = 8192;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Channel<GameLogEntry> pending = Channel.CreateBounded<GameLogEntry>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Task writer;
    private long droppedEntries;
    private long diskFailureCount;
    private string? lastError;
    private int disposed;

    public string Path { get; }
    public long DroppedEntries => Interlocked.Read(ref droppedEntries);
    public long DiskFailureCount => Interlocked.Read(ref diskFailureCount);
    public string? LastError => Volatile.Read(ref lastError);

    public GameLogMirrorWriter(string path)
    {
        Path = path;
        writer = Task.Run(WriteLoopAsync);
    }

    public bool TryWrite(GameLogEntry entry)
    {
        if (Volatile.Read(ref disposed) != 0 || !pending.Writer.TryWrite(entry))
        {
            RecordQueueDrop("Mirror queue full or closed.");
            return false;
        }
        return true;
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? output = null;
        try
        {
            await foreach (GameLogEntry entry in pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (output is null)
                    {
                        string? directory = System.IO.Path.GetDirectoryName(Path);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        output = new StreamWriter(new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                    }

                    await output.WriteLineAsync(JsonSerializer.Serialize(entry, Json)).ConfigureAwait(false);
                    await output.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    RecordDiskFailure($"{error.GetType().Name}: {error.Message}");
                    if (output is not null)
                    {
                        try { output.Dispose(); } catch { }
                        output = null;
                    }
                }
            }
        }
        catch (Exception error)
        {
            RecordDiskFailure($"{error.GetType().Name}: {error.Message}");
            while (pending.Reader.TryRead(out _)) RecordDiskFailure("Mirror writer stopped before writing a queued entry.");
        }
        finally
        {
            if (output is not null)
            {
                try { await output.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { RecordDiskFailure($"{error.GetType().Name}: {error.Message}"); }
            }
        }
    }

    private void RecordQueueDrop(string error)
    {
        Interlocked.Increment(ref droppedEntries);
        Volatile.Write(ref lastError, error);
    }

    private void RecordDiskFailure(string error)
    {
        Interlocked.Increment(ref diskFailureCount);
        Interlocked.Increment(ref droppedEntries);
        Volatile.Write(ref lastError, error);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        pending.Writer.TryComplete();
        try { writer.GetAwaiter().GetResult(); }
        catch (Exception error) { RecordDiskFailure($"{error.GetType().Name}: {error.Message}"); }
    }
}

internal sealed record GameLogsReadResponse(
    IReadOnlyList<GameLogEntry> Entries,
    long NextSequence,
    long OldestSequence,
    bool Truncated,
    long DroppedMemoryEntries,
    long DroppedMirrorEntries,
    long DiskFailureCount,
    IReadOnlyDictionary<string, long> SourceCounts,
    string MirrorPath,
    string? MirrorError,
    string? BootstrapLogPath,
    DateTimeOffset CaptureStartedAtUtc);
