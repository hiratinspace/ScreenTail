using System.Collections;
using System.Globalization;
using System.Text;

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
        private const string OriginalFormat = "{OriginalFormat}";
        private const string NullValue = "(null)";
        private const string ConnectionId = "ConnectionId";

        // Kestrel's bad-request lines quote what the client sent, from a request line with its query to a
        // header line with its token, under names such as {message} that say nothing about it. Every value
        // in this category is treated as content, except the connection id, which Kestrel makes up itself.
        private readonly bool _quotesRequests = category.StartsWith("Microsoft.AspNetCore.Server.Kestrel.BadRequests", StringComparison.Ordinal);

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
            var message = state is IReadOnlyList<KeyValuePair<string, object?>> pairs
                ? FromTemplate(pairs) ?? (_quotesRequests ? LogScrubber.Redacted : Withhold(pairs, formatter(state, exception)))
                : _quotesRequests ? LogScrubber.Redacted : formatter(state, exception);

            var line = $"{time.GetUtcNow():HH:mm:ss.fff} {Label(logLevel)} {category}: {LogScrubber.Scrub(message)}";
            lock (sink)
            {
                sink.WriteLine(line);
                for (var current = exception; current is not null; current = current.InnerException)
                {
                    sink.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {current.GetType().FullName}: {Describe(current)}"));
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

        // A bad request's message quotes the part of the request that could not be read, wherever the
        // exception is logged, so only its status code is written.
        private static string Describe(Exception exception) => exception is BadHttpRequestException bad
            ? string.Create(CultureInfo.InvariantCulture, $"{bad.StatusCode} {LogScrubber.Redacted}")
            : LogScrubber.Scrub(exception.Message);

        // The line is built from its template, so a value under a sensitive key is never written at all.
        // Looking for the value in the finished line instead depends on the order of the keys, on values
        // that overlap, and on the value being written as its own ToString(), and each of those can leak.
        // Null when there is no template. The template itself, which is a constant, when a value cannot
        // be placed.
        private string? FromTemplate(IReadOnlyList<KeyValuePair<string, object?>> pairs)
        {
            string? template = null;
            for (var i = pairs.Count - 1; i >= 0; i--)
            {
                if (pairs[i].Key == OriginalFormat)
                {
                    template = pairs[i].Value as string;
                    break;
                }
            }

            if (template is null)
            {
                return null;
            }

            var used = new bool[pairs.Count];
            var line = new StringBuilder(template.Length + 32);
            var at = 0;
            while (at < template.Length)
            {
                var brace = template.AsSpan(at).IndexOfAny('{', '}');
                if (brace < 0)
                {
                    _ = line.Append(template, at, template.Length - at);
                    break;
                }

                _ = line.Append(template, at, brace);
                at += brace;
                var twice = at + 1 < template.Length && template[at + 1] == template[at];
                if (template[at] == '}' || twice)
                {
                    _ = line.Append(template[at]);
                    at += twice ? 2 : 1;
                    continue;
                }

                var close = template.IndexOf('}', at + 1);
                if (close < 0)
                {
                    return template;
                }

                var hole = template.AsSpan(at + 1, close - at - 1);
                var split = hole.IndexOfAny(',', ':');
                var name = split < 0 ? hole : hole[..split];
                var index = Next(pairs, name, used);
                if (index < 0)
                {
                    return template;
                }

                used[index] = true;
                if (LogScrubber.IsSensitiveKey(pairs[index].Key) || (_quotesRequests && pairs[index].Key != ConnectionId))
                {
                    _ = line.Append(LogScrubber.Redacted);
                }
                else
                {
                    try
                    {
                        _ = line.AppendFormat(CultureInfo.InvariantCulture, string.Concat("{0", split < 0 ? [] : hole[split..], "}"), Shape(pairs[index].Value));
                    }
                    catch (FormatException)
                    {
                        return template;
                    }
                }

                at = close + 1;
            }

            return line.ToString();
        }

        // The next value logged under this name, in order, so a name the template uses twice takes both.
        private static int Next(IReadOnlyList<KeyValuePair<string, object?>> pairs, ReadOnlySpan<char> name, bool[] used)
        {
            for (var i = 0; i < pairs.Count; i++)
            {
                if (!used[i] && name.SequenceEqual(pairs[i].Key))
                {
                    return i;
                }
            }

            return -1;
        }

        // What the framework's own formatter writes for a value: (null), or a list joined with commas.
        private static object Shape(object? value)
        {
            switch (value)
            {
                case null:
                    return NullValue;
                case string text:
                    return text;
                case IEnumerable items:
                    var joined = new StringBuilder();
                    foreach (var item in items)
                    {
                        _ = joined.Append(joined.Length > 0 ? ", " : string.Empty).Append(CultureInfo.InvariantCulture, $"{item ?? NullValue}");
                    }

                    return joined.ToString();
                default:
                    return value;
            }
        }

        // No template, so the finished line is all there is: each sensitive value is cut out of it, longest
        // first, so a value inside another cannot break the longer match. When an earlier cut has already
        // broken a later value where it appears, part of it would be left behind, so the whole message is
        // withheld instead.
        private static string Withhold(IReadOnlyList<KeyValuePair<string, object?>> pairs, string message)
        {
            var values = new List<string>();
            foreach (var (key, value) in pairs)
            {
                if (LogScrubber.IsSensitiveKey(key) && value?.ToString() is { Length: > 0 } text)
                {
                    values.Add(text);
                }
            }

            if (values.Count == 0)
            {
                return message;
            }

            values.Sort((a, b) => b.Length.CompareTo(a.Length));
            var original = message;
            foreach (var text in values)
            {
                if (Occurrences(message, text) < Occurrences(original, text))
                {
                    return LogScrubber.Redacted;
                }

                message = message.Replace(text, LogScrubber.Redacted, StringComparison.Ordinal);
            }

            return message;
        }

        private static int Occurrences(string message, string text)
        {
            var count = 0;
            for (var at = message.IndexOf(text, StringComparison.Ordinal); at >= 0; at = message.IndexOf(text, at + text.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
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
