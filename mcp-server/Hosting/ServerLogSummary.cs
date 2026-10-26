using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace AmongLLM.Server.Hosting;

public static partial class ServerLogSummary
{
    private const int MaximumSummaryLength = 768;
    private const int MaximumPropertyCount = 12;
    private const int MaximumSafeTextLength = 192;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions LogStringJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly HashSet<string> SafeNumericProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "targetNetId", "callId", "targetClientId", "netObjectId", "instanceId", "x", "y", "textBoxInstanceId",
        "afterSequence", "limit", "timeoutMs", "milliseconds", "frame", "serverIndex"
    };
    private static readonly Dictionary<string, HashSet<string>> SafeEnumProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sendOption"] = new(StringComparer.OrdinalIgnoreCase) { "Reliable", "Unreliable" },
        ["mode"] = new(StringComparer.OrdinalIgnoreCase) { "rpc", "raw" },
        ["state"] = new(StringComparer.OrdinalIgnoreCase) { "foreground", "restore", "background" },
        ["kind"] = new(StringComparer.OrdinalIgnoreCase) { "click", "wait", "waitForButton" },
        ["gameMode"] = new(StringComparer.OrdinalIgnoreCase) { "Normal", "HideNSeek" }
    };
    private static readonly HashSet<string> RpcNumericTypes = new(StringComparer.Ordinal)
    {
        "byte", "sbyte", "int16", "uint16", "int32", "uint32", "packedInt32", "packedUInt32", "float"
    };
    private static readonly HashSet<string> SafeStringProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenId", "buttonId", "member", "name", "path", "labelContains"
    };
    private static readonly HashSet<string> SafeStringArrayProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "members", "levels", "sources"
    };

    private static readonly HashSet<string> SafeBooleanResults = new(StringComparer.OrdinalIgnoreCase)
    {
        "success", "submitted", "accepted", "changed"
    };

    private static readonly HashSet<string> SafeNumericResults = new(StringComparer.OrdinalIgnoreCase)
    {
        "stepsCompleted", "totalSteps", "fieldCount", "netId", "netObjectId", "callId", "targetClientId", "processId", "frame"
    };

    public static string SummarizePayload(object payload)
        => SummarizePayload(null, payload);

    public static string SummarizePayload(string? operation, object payload)
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(payload, Json);
            if (operation is "send_rpc" or "send_hazel_message")
                return Bound(SummarizeHazelPayload(element, operation == "send_rpc"));
            if (operation == "snap_to")
                return Bound(SummarizeSnapToPayload(element));
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
            foreach (var property in result.EnumerateObject())
            {
                if (IsSensitiveName(property.Name))
                    continue;
                if (SafeBooleanResults.Contains(property.Name) && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    safeFields.Add($"{property.Name}={property.Value.GetBoolean().ToString().ToLowerInvariant()}");
                else if (SafeNumericResults.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var number))
                    safeFields.Add($"{property.Name}={number}");
            }
            var known = safeFields.Count == 0 ? "object; values omitted" : string.Join(", ", safeFields.Take(MaximumPropertyCount));
            return Bound(known);
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

        var redacted = SensitiveValuePattern().Replace(value, "$1[REDACTED]");
        redacted = BearerTokenPattern().Replace(redacted, "Bearer [REDACTED]");
        return Bound(EscapeControlCharacters(redacted));
    }

    private static string SummarizeSnapToPayload(JsonElement payload)
    {
        var fields = new List<string>();
        AddSafeNumber(payload, "netObjectId", fields);
        AddSafeNumber(payload, "x", fields);
        AddSafeNumber(payload, "y", fields);
        return fields.Count == 0 ? "snap_to parameters unavailable" : string.Join("; ", fields);
    }

    private static string SummarizeHazelPayload(JsonElement payload, bool isSendRpc)
    {
        var fields = new List<string>();
        if (!isSendRpc && payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String)
            fields.Add($"mode={SanitizeText(mode.GetString())}");

        AddSafeNumber(payload, "targetNetId", fields);
        AddSafeNumber(payload, "callId", fields);
        AddSafeEnum(payload, "sendOption", fields);
        AddSafeNumber(payload, "targetClientId", fields);

        var fieldsValue = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("fields", out var fieldArray) && fieldArray.ValueKind == JsonValueKind.Array
            ? fieldArray
            : default;
        if (fieldsValue.ValueKind == JsonValueKind.Array)
            fields.Add($"fields=[{SummarizeRpcFields(fieldsValue)}]");
        else
            fields.Add("fields=unavailable");

        return string.Join("; ", fields);
    }

    private static string SummarizeRpcFields(JsonElement fields)
    {
        var summaries = new List<string>();
        var count = 0;
        foreach (var field in fields.EnumerateArray())
        {
            if (summaries.Count < MaximumPropertyCount)
                summaries.Add(SummarizeRpcField(field, count));
            count++;
        }

        if (count > summaries.Count)
            summaries.Add($"+{count - summaries.Count} more");
        return string.Join(", ", summaries);
    }

    private static string SummarizeRpcField(JsonElement field, int index)
    {
        if (field.ValueKind != JsonValueKind.Object || !field.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
            return $"field#{index}=invalid";

        var type = SanitizeText(typeValue.GetString());
        if (typeValue.GetString() == "start")
        {
            if (field.TryGetProperty("tag", out var tag) && tag.ValueKind == JsonValueKind.Number && tag.TryGetByte(out var tagNumber))
                return $"start(tag={tagNumber})";
            return "start(tag=unknown)";
        }
        if (typeValue.GetString() == "end")
            return "end";
        if (!field.TryGetProperty("value", out var value))
            return $"{type}=missing";

        if (typeValue.GetString() == "bool" && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return $"bool={value.GetBoolean().ToString().ToLowerInvariant()}";
        if (RpcNumericTypes.Contains(typeValue.GetString() ?? "") && value.ValueKind == JsonValueKind.Number)
            return $"{type}={value.GetRawText()}";
        if (typeValue.GetString() == "vector2" && TryReadVector2(value, out var x, out var y))
            return $"vector2=({FormatNumber(x)},{FormatNumber(y)})";
        if (typeValue.GetString() == "string" && value.ValueKind == JsonValueKind.String)
        {
            return $"string={FormatSafeString(value.GetString())}";
        }
        if (typeValue.GetString() == "bytes" && value.ValueKind == JsonValueKind.String)
            return $"bytes=<omitted; base64Chars={(value.GetString() ?? "").Length}>";
        return $"{type}=omitted";
    }

    private static bool TryReadVector2(JsonElement value, out float x, out float y)
    {
        x = y = 0;
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("x", out var xValue) && xValue.TryGetSingle(out x) && float.IsFinite(x) &&
            value.TryGetProperty("y", out var yValue) && yValue.TryGetSingle(out y) && float.IsFinite(y);
    }

    private static string FormatNumber(float value) => value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);

    private static void AddSafeNumber(JsonElement value, string name, List<string> fields)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
            SafeNumericProperties.Contains(name) && property.ValueKind == JsonValueKind.Number)
            fields.Add($"{name}={property.GetRawText()}");
    }

    private static void AddSafeEnum(JsonElement value, string name, List<string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            !SafeEnumProperties.TryGetValue(name, out var allowed) || property.GetString() is not { } text || !allowed.Contains(text))
            return;
        fields.Add($"{name}={text}");
    }

    private static string EscapeControlCharacters(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsControl(character))
                builder.Append($"\\u{(int)character:X4}");
            else
                builder.Append(character);
        }
        return builder.ToString();
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
        if (propertyName is not null && SafeNumericProperties.Contains(propertyName) && value.ValueKind == JsonValueKind.Number)
            return value.GetRawText();
        if (propertyName is not null && SafeStringProperties.Contains(propertyName) && value.ValueKind == JsonValueKind.String)
            return FormatSafeString(value.GetString());
        if (propertyName is not null && SafeStringArrayProperties.Contains(propertyName) && value.ValueKind == JsonValueKind.Array)
            return DescribeSafeStringArray(value);
        if (propertyName is not null && SafeEnumProperties.TryGetValue(propertyName, out var allowed) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } enumValue && allowed.Contains(enumValue))
            return enumValue;
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

    private static string DescribeSafeStringArray(JsonElement value)
    {
        var items = new List<string>();
        var count = value.GetArrayLength();
        foreach (var item in value.EnumerateArray().Take(6))
            items.Add(item.ValueKind == JsonValueKind.String ? FormatSafeString(item.GetString()) : DescribeKind(item));
        var suffix = count > items.Count ? $",+{count - items.Count} more" : "";
        return $"array(count={count};values=[{string.Join(",", items)}{suffix}])";
    }

    private static string FormatSafeString(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        if (IsSensitiveName(value))
            return "[redacted]";
        return JsonSerializer.Serialize(Bound(SanitizeText(value), MaximumSafeTextLength), LogStringJson);
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
        value.Contains("dateofbirth", StringComparison.OrdinalIgnoreCase) ||
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

    [GeneratedRegex("(?i)\\bBearer\\s+[A-Za-z0-9._~+/-]{8,}={0,2}")]
    private static partial Regex BearerTokenPattern();
}
