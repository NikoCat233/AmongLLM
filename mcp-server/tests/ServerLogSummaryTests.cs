using System.Text.Json;
using AmongLLM.Server.Hosting;

namespace mcp_server.Tests;

public sealed class ServerLogSummaryTests
{
    [Fact]
    public void PayloadSummaryOmitsCredentialCodeMessageAndByteValues()
    {
        const string token = "itch-key-do-not-log";
        const string source = "private-csharp-source-do-not-log";
        const string chat = "private-chat-message-do-not-log";
        const string bytes = "base64-private-bytes-do-not-log";
        var summary = ServerLogSummary.SummarizePayload(new
        {
            platformAuthToken = token,
            UserIDToken = "eos-user-token-do-not-log",
            code = source,
            message = chat,
            bytes
        });

        Assert.DoesNotContain(token, summary, StringComparison.Ordinal);
        Assert.DoesNotContain("eos-user-token-do-not-log", summary, StringComparison.Ordinal);
        Assert.DoesNotContain(source, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(chat, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(bytes, summary, StringComparison.Ordinal);
        Assert.DoesNotContain("platformAuthToken", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UserIDToken", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", summary, StringComparison.Ordinal);
        Assert.True(summary.Length <= 768);
    }

    [Fact]
    public void ScreenshotAndGameLogSummariesAreBoundedAndOmitContents()
    {
        var imageData = new string('A', 2_000_000);
        using var screenshot = JsonDocument.Parse(JsonSerializer.Serialize(new { pngBase64 = imageData }));
        var screenshotSummary = ServerLogSummary.SummarizeResult("capture_screenshot", screenshot.RootElement);
        Assert.Contains("image omitted", screenshotSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(imageData, screenshotSummary, StringComparison.Ordinal);
        Assert.True(screenshotSummary.Length <= 768);

        const string logMessage = "private unity log text do-not-log";
        const string secret = "very-secret-token-value";
        using var logs = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            entries = new[] { new { sequence = 11, source = "Unity", level = "Warning", message = logMessage } },
            nextSequence = 12,
            oldestSequence = 3,
            truncated = true,
            mirrorPath = "C:\\Users\\Example\\AppData\\Local\\AmongLLM\\logs\\game.jsonl",
            mirrorError = $"mirror write failed: token={secret}"
        }));
        var logsSummary = ServerLogSummary.SummarizeResult("read_game_logs", logs.RootElement);
        Assert.Contains("entries=1", logsSummary, StringComparison.Ordinal);
        Assert.Contains("nextSequence=12", logsSummary, StringComparison.Ordinal);
        Assert.Contains("oldestSequence=3", logsSummary, StringComparison.Ordinal);
        Assert.Contains("mirrorPath=C:\\Users\\Example", logsSummary, StringComparison.Ordinal);
        Assert.Contains("mirrorError=", logsSummary, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", logsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(logMessage, logsSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, logsSummary, StringComparison.Ordinal);
        Assert.True(logsSummary.Length <= 768);
    }

    [Fact]
    public void SensitiveResultOperationsOnlyLogTypeAndKeyCount()
    {
        using var result = JsonDocument.Parse("{\"result\":\"arbitrary private result\",\"token\":\"secret\"}");
        var summary = ServerLogSummary.SummarizeResult("execute_csharp", result.RootElement);
        Assert.Equal("object; keyCount=2; values omitted", summary);
        Assert.DoesNotContain("arbitrary private result", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", summary, StringComparison.Ordinal);
    }
}
