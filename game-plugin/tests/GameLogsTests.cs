using System.Text.Json;

namespace AmongLLM.GamePlugin.Tests;

public sealed class GameLogsTests
{
    [Fact]
    public void PublicAfterSequenceCursorDoesNotRepeatAndSupportsMultipleFilters()
    {
        var buffer = new GameLogBuffer();
        buffer.Append("Unity", "Info", "one");
        buffer.Append("TOHE", "Error", "two");
        buffer.Append("Unity", "Warning", "three");
        var first = buffer.ReadAfter(0, 1, new[] { "unity", "tohe" }, new[] { "Error", "Warning" });
        Assert.Equal(2, first.Entries.Single().Seq);
        Assert.Equal(2, first.NextSequence);
        var second = buffer.ReadAfter(first.NextSequence, 1, new[] { "Unity", "TOHE" }, new[] { "Error", "Warning" });
        Assert.Equal(3, second.Entries.Single().Seq);
        Assert.Empty(buffer.ReadAfter(second.NextSequence).Entries);
    }

    [Fact]
    public void RingReportsEvictionAndCursorTruncation()
    {
        var buffer = new GameLogBuffer(capacity: 3);
        for (int i = 1; i <= 5; i++) buffer.Append("Game", "Info", $"message-{i}");

        GameLogReadPage page = buffer.Read(cursor: 1, limit: 10);

        Assert.True(page.Truncated);
        Assert.Equal(3, page.OldestSequence);
        Assert.Equal(6, page.NextSequence);
        Assert.Equal(2, page.DroppedMemoryEntries);
        Assert.Equal(new long[] { 3, 4, 5 }, page.Entries.Select(entry => entry.Seq));
    }

    [Fact]
    public void FilteredPagesResumeAcrossSequenceGaps()
    {
        var buffer = new GameLogBuffer(capacity: 10);
        buffer.Append("Plugin", "Info", "one");
        buffer.Append("Unity", "Info", "two");
        buffer.Append("Plugin", "Error", "three");
        buffer.Append("Unity", "Warning", "four");

        GameLogReadPage first = buffer.Read(cursor: 1, limit: 1, source: "unity", level: "info");
        Assert.Equal(new long[] { 2 }, first.Entries.Select(entry => entry.Seq));
        Assert.Equal(3, first.NextSequence);

        GameLogReadPage second = buffer.Read(cursor: first.NextSequence, limit: 1, source: "Unity");
        Assert.Equal(new long[] { 4 }, second.Entries.Select(entry => entry.Seq));
        Assert.Equal(5, second.NextSequence);

        GameLogReadPage errors = buffer.Read(cursor: 1, limit: 10, level: "ERROR");
        Assert.Equal(new long[] { 3 }, errors.Entries.Select(entry => entry.Seq));
        Assert.Equal(2, errors.SourceCounts["Plugin"]);
        Assert.Equal(2, errors.SourceCounts["Unity"]);
    }

    [Fact]
    public void ConcurrentAppendsKeepUniqueSequencesAndCompleteSourceCounts()
    {
        const int count = 1200;
        var buffer = new GameLogBuffer(capacity: count);
        Parallel.For(0, count, index => buffer.Append(index % 2 == 0 ? "Unity" : "Plugin", "Info", index.ToString()));

        GameLogReadPage first = buffer.Read(limit: 500);
        GameLogReadPage second = buffer.Read(first.NextSequence, limit: 500);
        GameLogReadPage third = buffer.Read(second.NextSequence, limit: 500);
        long[] sequences = first.Entries.Concat(second.Entries).Concat(third.Entries).Select(entry => entry.Seq).ToArray();

        Assert.Equal(count, sequences.Length);
        Assert.Equal(Enumerable.Range(1, count).Select(number => (long)number), sequences);
        Assert.Equal(count, first.SourceCounts.Values.Sum());
        Assert.Equal(count / 2, first.SourceCounts["Unity"]);
        Assert.Equal(count / 2, first.SourceCounts["Plugin"]);
        Assert.Equal(0, first.DroppedMemoryEntries);
    }

    [Fact]
    public void JsonlMirrorPreservesSequenceOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AmongLLM-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "session.jsonl");
        var buffer = new GameLogBuffer(capacity: 20);
        var mirror = new GameLogMirrorWriter(path);

        for (int i = 0; i < 10; i++) mirror.TryWrite(buffer.Append("Game", "Info", $"message-{i}"));
        mirror.Dispose();

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(10, lines.Length);
        long[] sequences = lines.Select(line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("seq").GetInt64();
        }).ToArray();
        Assert.Equal(Enumerable.Range(1, 10).Select(number => (long)number), sequences);
        Assert.Equal(0, mirror.DroppedEntries);
        Assert.Null(mirror.LastError);

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void JsonlMirrorSurfacesDiskFailuresAndDroppedEntries()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AmongLLM-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var mirror = new GameLogMirrorWriter(directory);
        mirror.TryWrite(new GameLogEntry(1, DateTimeOffset.UtcNow, "Game", "Error", "fail", null));

        mirror.Dispose();

        Assert.Equal(1, mirror.DroppedEntries);
        Assert.Equal(1, mirror.DiskFailureCount);
        Assert.False(string.IsNullOrWhiteSpace(mirror.LastError));
        Directory.Delete(directory, recursive: true);
    }
}
