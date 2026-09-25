using System.Globalization;

namespace ScreenTail.Api.Logging;

/// <summary>The API's one log sink: a scrubbed line per event, in place of the framework's providers (ST-011).</summary>
public sealed class ScrubbingLoggerProvider(TextWriter sink, LogLevel minimum, TimeProvider? time = null) : ILoggerProvider
{
    private readonly TextWriter _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ILogger CreateLogger(string categoryName) => new ScrubbingLogger(categoryName, _sink, minimum, _time);

    public void Dispose()
    {
        // The sink is the caller's.
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
