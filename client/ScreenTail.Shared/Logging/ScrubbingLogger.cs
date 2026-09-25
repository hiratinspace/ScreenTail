using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ScreenTail.Shared.Logging;

/// <summary>
/// The one log sink every ScreenTail process uses (ST-011): a line per event, scrubbed by
/// <see cref="LogScrubber"/> before it is written. Installed in place of the framework's providers so
/// there is no second path a value can take to a file.
///
/// A value logged under a content-carrying key is replaced in the formatted line by its redaction; the
/// whole line and any exception then pass through the path and address scrubber. Exceptions are written
/// with their type, their scrubbed message and their scrubbed trace, one indented line each.
/// </summary>
public sealed class ScrubbingLoggerProvider(TextWriter sink, LogLevel minimum, TimeProvider? time = null) : ILoggerProvider
{
    private readonly TextWriter _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ILogger CreateLogger(string categoryName) => new ScrubbingLogger(categoryName, _sink, minimum, _time);

    public void Dispose()
    {
        // The sink is the caller's (stderr, a file they own); closing it here would close it under them.
    }

    private sealed class ScrubbingLogger(string category, TextWriter sink, LogLevel minimum, TimeProvider time) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    if (LogScrubber.IsSensitiveKey(key) && value?.ToString() is { Length: > 0 } text)
                    {
                        message = message.Replace(text, LogScrubber.Redacted, StringComparison.Ordinal);
                    }
                }
            }

            var line = $"{time.GetUtcNow():HH:mm:ss.fff} {Label(logLevel)} {category}: {LogScrubber.Scrub(message)}";
            lock (sink)
            {
                sink.WriteLine(line);
                for (var current = exception; current is not null; current = current.InnerException)
                {
                    sink.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {current.GetType().FullName}: {LogScrubber.Scrub(current.Message)}"));
                    if (current.StackTrace is { } trace)
                    {
                        foreach (var frame in trace.Split('\n'))
                        {
                            sink.WriteLine("    " + LogScrubber.Scrub(frame.Trim()));
                        }
                    }
                }

                sink.Flush();
            }
        }

        private static string Label(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "FATAL",
            _ => level.ToString().ToUpperInvariant(),
        };
    }
}

/// <summary>The minimum level, from a setting: <c>SCREENTAIL_LOG_LEVEL</c>. Information unless said otherwise (ST-011 AC3).</summary>
public static class LogLevels
{
    public const string Variable = "SCREENTAIL_LOG_LEVEL";

    public static LogLevel Parse(string? value) =>
        Enum.TryParse<LogLevel>(value?.Trim(), ignoreCase: true, out var level) && level != LogLevel.None ? level : LogLevel.Information;
}
