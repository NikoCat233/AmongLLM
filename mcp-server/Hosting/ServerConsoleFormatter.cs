using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace AmongLLM.Server.Hosting;

public sealed class ServerConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "amongllm";

    public override void Write<TState>(in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
            return;

        var prefix = $"[{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] [{logEntry.LogLevel}] ";
        WriteLines(textWriter, prefix, message);
        if (logEntry.Exception is not null)
            WriteLines(textWriter, prefix, logEntry.Exception.ToString());
    }

    private static void WriteLines(TextWriter writer, string prefix, string value)
    {
        using var lines = new StringReader(value);
        while (lines.ReadLine() is { } line)
        {
            writer.Write(prefix);
            writer.WriteLine(line);
        }
    }
}
