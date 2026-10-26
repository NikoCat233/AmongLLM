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

    [Fact]
    public void RpcPayloadSummaryShowsUsefulTypedValuesButOmitsBytesAndRedactsCredentials()
    {
        using var payload = JsonDocument.Parse("""
            {
              "targetNetId": 17,
              "callId": 42,
              "sendOption": "Reliable",
              "targetClientId": -1,
              "fields": [
                { "type": "start", "tag": 5 },
                { "type": "int32", "value": 123 },
                { "type": "vector2", "value": { "x": 1.25, "y": -2.5 } },
                { "type": "string", "value": "CrewmateName" },
                { "type": "string", "value": "Bearer eyJhbGciOiJIUzI1NiJ9.signature" },
                { "type": "bytes", "value": "AAEC" },
                { "type": "end" }
              ]
            }
            """);

        var summary = ServerLogSummary.SummarizePayload("send_rpc", payload.RootElement);

        Assert.Contains("targetNetId=17", summary, StringComparison.Ordinal);
        Assert.Contains("callId=42", summary, StringComparison.Ordinal);
        Assert.Contains("sendOption=Reliable", summary, StringComparison.Ordinal);
        Assert.Contains("targetClientId=-1", summary, StringComparison.Ordinal);
        Assert.Contains("start(tag=5)", summary, StringComparison.Ordinal);
        Assert.Contains("int32=123", summary, StringComparison.Ordinal);
        Assert.Contains("vector2=(1.25,-2.5)", summary, StringComparison.Ordinal);
        Assert.Contains("CrewmateName", summary, StringComparison.Ordinal);
        Assert.Contains("bytes=<omitted", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("AAEC", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", summary, StringComparison.Ordinal);
        Assert.True(summary.Length <= 768);
    }

    [Fact]
    public void SnapToSummaryShowsNetworkObjectAndCoordinates()
    {
        var summary = ServerLogSummary.SummarizePayload("snap_to", new { netObjectId = 31, x = -4.25f, y = 12.5f });

        Assert.Equal("netObjectId=31; x=-4.25; y=12.5", summary);
    }

    [Fact]
    public void RpcRichTextNameIsReadableAndNotTruncatedAtEightyCharacters()
    {
        const string name = "<color=#902efd>Host</color><color=#4bf4ff>♥</color><color=#ffc0cb>测试玩家名字完整显示</color>";
        Assert.True(name.Length > 80);
        var summary = ServerLogSummary.SummarizePayload("send_rpc", new
        {
            targetNetId = 4, callId = 6, sendOption = "Reliable", targetClientId = -1,
            fields = new[] { new { type = "string", value = name } }
        });
        Assert.Contains(name, summary);
        Assert.DoesNotContain("\\u003C", summary);
    }

    [Fact]
    public void SanitizerEscapesControlsAndRedactsCredentialAssignments()
    {
        var summary = ServerLogSummary.SanitizeText("room\r\n\t\u001b token=hidden-value");

        Assert.Equal("room\\u000D\\u000A\\u0009\\u001B token=[REDACTED]", summary);
    }

    [Fact]
    public void PayloadSummaryShowsSelectedUiAndDiagnosticIdentifiersOnly()
    {
        using var click = JsonDocument.Parse("""
            {
              "timeoutMs": 5000,
              "steps": [{ "kind": "click", "selector": { "name": "Start Game", "path": "Lobby/StartButton", "labelContains": "Start" } }]
            }
            """);
        var clickSummary = ServerLogSummary.SummarizePayload("click_sequence", click.RootElement);
        Assert.Contains("timeoutMs:5000", clickSummary, StringComparison.Ordinal);
        Assert.Contains("name:\"Start Game\"", clickSummary, StringComparison.Ordinal);
        Assert.Contains("path:\"Lobby/StartButton\"", clickSummary, StringComparison.Ordinal);
        Assert.Contains("labelContains:\"Start\"", clickSummary, StringComparison.Ordinal);

        using var read = JsonDocument.Parse("""
            { "netObjectId": 9, "members": ["OwnerId", "EOSManager.platformAuthToken"] }
            """);
        var readSummary = ServerLogSummary.SummarizePayload("read_net_object_properties", read.RootElement);
        Assert.Contains("netObjectId:9", readSummary, StringComparison.Ordinal);
        Assert.Contains("OwnerId", readSummary, StringComparison.Ordinal);
        Assert.Contains("[redacted]", readSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("platformAuthToken", readSummary, StringComparison.OrdinalIgnoreCase);
    }
}
