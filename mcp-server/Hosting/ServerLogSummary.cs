using System.Text.Json;
using System.Text.RegularExpressions;

namespace AmongLLM.Server.Hosting;

public static partial class ServerLogSummary
{
    private const int MaximumSummaryLength = 768;
    private const int MaximumPropertyCount = 12;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> SafeBooleanResults = new(StringComparer.OrdinalIgnoreCase)
    {
        "success", "submitted", "accepted", "changed"
    };

    private static readonly HashSet<string> SafeNumericResults = new(StringComparer.OrdinalIgnoreCase)
    {
        "stepsCompleted", "totalSteps", "fieldCount", "netId", "netObjectId", "callId", "targetClientId", "processId", "frame"
    };

    public static string SummarizePayload(object payload)
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(payload, Json);
            return Bound(DescribeShape(element, null, 0));
        }
        catch
        {
            return "payload shape unavailable";
        }
    }

    public static string SummarizeResult(string operation, JsonElement result)
    {
        if (operation == "capture_screenshot")
        {
            var encoded = ReadString(result, "pngBase64", "imageBase64", "base64", "data");
            return encoded is null ? "image omitted" : $"image omitted; base64Characters={encoded.Length}";
        }

        if (operation == "read_game_logs")
            return Bound(SummarizeGameLogs(result));

        if (operation is "execute_csharp" or "read_net_object_properties")
            return Bound($"{DescribeKind(result)}; keyCount={GetKeyCount(result)}; values omitted");

        if (result.ValueKind == JsonValueKind.Object)
        {
            var safeFields = new List<string>();
            var keyCount = 0;
            foreach (var property in result.EnumerateObject())
            {
                keyCount++;
                if (IsSensitiveName(property.Name))
                    continue;
                if (SafeBooleanResults.Contains(property.Name) && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    safeFields.Add($"{property.Name}={property.Value.GetBoolean().ToString().ToLowerInvariant()}");
                else if (SafeNumericResults.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var number))
                    safeFields.Add($"{property.Name}={number}");
            }
            var known = safeFields.Count == 0 ? "none" : string.Join(",", safeFields.Take(MaximumPropertyCount));
            return Bound($"object; keyCount={keyCount}; safeFields={known}");
        }

        return result.ValueKind switch
        {
            JsonValueKind.Array => $"array; count={result.GetArrayLength()}; values omitted",
            JsonValueKind.String => "string; content omitted",
            JsonValueKind.Number => "number; value omitted",
            JsonValueKind.True or JsonValueKind.False => $"boolean; value={result.GetBoolean().ToString().ToLowerInvariant()}",
            JsonValueKind.Null or JsonValueKind.Undefined => "null",
            _ => DescribeKind(result)
        };
    }

    public static string SanitizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "none";

        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return Bound(SensitiveValuePattern().Replace(singleLine, "$1[REDACTED]"));
    }

    private static string SummarizeGameLogs(JsonElement result)
    {
        var entries = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("entries", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetArrayLength()
            : 0;
        var nextSequence = ReadNumber(result, "nextSequence");
        var oldestSequence = ReadNumber(result, "oldestSequence");
        var truncated = ReadBoolean(result, "truncated");
        var mirrorPath = ReadString(result, "mirrorPath");
        var mirrorError = ReadString(result, "mirrorError");
        return $"entries={entries}; nextSequence={nextSequence}; oldestSequence={oldestSequence}; truncated={truncated}; mirrorPath={SanitizePath(mirrorPath)}; mirrorError={SanitizeText(mirrorError)}";
    }

    private static string DescribeShape(JsonElement value, string? propertyName, int depth)
    {
        if (propertyName is not null && IsSensitiveName(propertyName))
            return "redacted";
        if (depth >= 4)
            return DescribeKind(value);

        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new List<string>();
            var total = 0;
            foreach (var property in value.EnumerateObject())
            {
                total++;
                if (fields.Count < MaximumPropertyCount)
                {
                    var key = IsSensitiveName(property.Name) ? "[redacted]" : Bound(property.Name, 48);
                    fields.Add($"{key}:{DescribeShape(property.Value, property.Name, depth + 1)}");
                }
            }
            var suffix = total > fields.Count ? $",+{total - fields.Count} more" : "";
            return $"object({string.Join(",", fields)}{suffix})";
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var samples = new List<string>();
            foreach (var item in value.EnumerateArray().Take(2))
                samples.Add(DescribeShape(item, propertyName, depth + 1));
            var items = samples.Count == 0 ? "" : $",items={string.Join("|", samples.Distinct())}";
            return $"array(count={value.GetArrayLength()}{items})";
        }

        return DescribeKind(value);
    }

    private static int GetKeyCount(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().Count(),
            JsonValueKind.Array => value.GetArrayLength(),
            _ => 0
        };

    private static string DescribeKind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        _ => "value"
    };

    private static string? ReadString(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        return null;
    }

    private static string ReadNumber(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.TryGetInt64(out var number)
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "unknown";

    private static string ReadBoolean(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean().ToString().ToLowerInvariant()
            : "unknown";

    private static string SanitizePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "none" : Bound(SanitizeText(value), 192);

    private static bool IsSensitiveName(string value) =>
        value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("apikey", StringComparison.OrdinalIgnoreCase);

    private static string Bound(string value, int limit = MaximumSummaryLength) =>
        value.Length <= limit ? value : value[..Math.Max(0, limit - 1)] + "…";

    [GeneratedRegex("(?i)((?:token|auth(?:orization)?|secret|password|api[_ -]?key)\\b[^:=]{0,16}[:=]\\s*)(?:Bearer\\s+)?[^\\s,;]+")]
    private static partial Regex SensitiveValuePattern();
}
