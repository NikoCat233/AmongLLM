using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace AmongLLM.Server.Hosting;

public sealed class ServerConsoleFormatter(IOptions<ServerConsoleFormatterOptions> options) : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "amongllm";
    private readonly LoggerColorBehavior _colorBehavior = options.Value.ColorBehavior;
    internal LoggerColorBehavior ColorBehavior => _colorBehavior;

    public override void Write<TState>(in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var formattedMessage = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(formattedMessage) && logEntry.Exception is null)
            return;

        var useColor = ShouldUseColor(_colorBehavior, Console.IsOutputRedirected,
            Environment.GetEnvironmentVariable("NO_COLOR"), Environment.GetEnvironmentVariable("TERM"));
        var prefix = $"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] [{logEntry.LogLevel}] ";
        var levelColor = useColor ? LevelColor(logEntry.LogLevel) : null;
        var outcomeColor = useColor ? OutcomeColor(logEntry.EventId) : null;
        WriteLines(textWriter, prefix, EscapeControlCharacters(formattedMessage), levelColor, outcomeColor);
        if (logEntry.Exception is not null)
            WriteLines(textWriter, prefix, EscapeControlCharacters(logEntry.Exception.ToString()), levelColor, outcomeColor);
    }

    internal static bool ShouldUseColor(LoggerColorBehavior colorBehavior, bool outputRedirected, string? noColor, string? term)
    {
        if (colorBehavior == LoggerColorBehavior.Disabled || noColor is not null || string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
            return false;
        return colorBehavior == LoggerColorBehavior.Enabled || !outputRedirected;
    }

    private static void WriteLines(TextWriter writer, string prefix, string value, string? levelColor, string? outcomeColor)
    {
        using var lines = new StringReader(value);
        while (lines.ReadLine() is { } line)
        {
            if (levelColor is null)
                writer.Write(prefix);
            else
                writer.Write($"{levelColor}{prefix}\u001b[0m");
            writer.Write(outcomeColor is null ? line : ColorOutcome(line, outcomeColor));
            writer.WriteLine();
        }
    }

    private static string? LevelColor(LogLevel level) => level switch
    {
        LogLevel.Trace => "\u001b[90m",
        LogLevel.Debug => "\u001b[90m",
        LogLevel.Information => "\u001b[36m",
        LogLevel.Warning => "\u001b[33m",
        LogLevel.Error => "\u001b[31m",
        LogLevel.Critical => "\u001b[1;31m",
        _ => null
    };

    private static string? OutcomeColor(EventId eventId)
    {
        if (eventId.Id == ServerLogEvents.RegistrationRejected.Id ||
            eventId.Id == ServerLogEvents.CommandClientError.Id ||
            eventId.Id == ServerLogEvents.CommandTransportError.Id)
            return "\u001b[31m";
        if (eventId.Id == ServerLogEvents.CommandTimedOut.Id || eventId.Id == ServerLogEvents.CommandCancelled.Id)
            return "\u001b[33m";
        return eventId.Id == ServerLogEvents.CommandCompleted.Id ? "\u001b[32m" : null;
    }

    private static string ColorOutcome(string line, string color)
    {
        const string marker = "outcome=";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return line;
        var valueStart = start + marker.Length;
        var valueEnd = valueStart;
        while (valueEnd < line.Length && !char.IsWhiteSpace(line[valueEnd]) && line[valueEnd] is not (';' or ',' or '.'))
            valueEnd++;
        if (valueEnd == valueStart)
            return line;
        return string.Concat(line[..valueStart], color, line[valueStart..valueEnd], "\u001b[0m", line[valueEnd..]);
    }

    private static string EscapeControlCharacters(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\r' or '\n')
                builder.Append(character);
            else if (char.IsControl(character))
                builder.Append($"\\u{(int)character:X4}");
            else
                builder.Append(character);
        }
        return builder.ToString();
    }
}

public sealed class ServerConsoleFormatterOptions : ConsoleFormatterOptions
{
    public LoggerColorBehavior ColorBehavior { get; set; } = LoggerColorBehavior.Default;
}
