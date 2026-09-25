using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ScreenTail.Shared.Logging;

/// <summary>
/// What a crash leaves behind (ST-011 AC2): the exception types and the stack frames, and nothing else.
///
/// The message is left out on purpose. It is where a path, a window title or a customer's name ends up
/// when a framework describes what it could not do, and a report that might carry one cannot be sent
/// anywhere. Frames are type and method names with a line number: where it broke, not what it was
/// looking at.
///
/// Written to a local queue only when the technician opted in, and sent by nothing yet: the queue is a
/// folder, and what reads it is ST-098's. Off by default, because a report nobody asked for is a report
/// nobody reviewed.
/// </summary>
public static class CrashReport
{
    public const string Variable = "SCREENTAIL_CRASH_REPORTS";

    /// <summary>Where the queue lives: beside the store, under the user's local app data.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTail",
        "crashes");

    /// <summary>The opt-in, read from the environment: <c>1</c> or <c>true</c> means yes. Anything else, including nothing, means no.</summary>
    public static bool Enabled(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var value = environment(Variable)?.Trim();
        return value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    public static string Render(Exception exception, string product, string version)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var report = new StringBuilder();
        _ = report.AppendLine(CultureInfo.InvariantCulture, $"{product} {version}");
        _ = report.AppendLine(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O}");
        _ = report.AppendLine(CultureInfo.InvariantCulture, $"{Environment.OSVersion} .NET {Environment.Version}");
        _ = report.AppendLine();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            _ = report.AppendLine(current.GetType().FullName);
            foreach (var frame in new StackTrace(current, fNeedFileInfo: true).GetFrames())
            {
                var method = frame.GetMethod();
                if (method is null)
                {
                    continue;
                }

                var line = frame.GetFileLineNumber();
                _ = report.AppendLine(CultureInfo.InvariantCulture, $"  at {method.DeclaringType?.FullName}.{method.Name}{(line > 0 ? $":{line}" : string.Empty)}");
            }

            if (current.InnerException is not null)
            {
                _ = report.AppendLine("caused by");
            }
        }

        return report.ToString();
    }

    /// <summary>
    /// Queues a report, or does nothing when not opted in. Returns the file written, or null. Never
    /// throws: this runs while the process is already dying, and a second failure here would hide the
    /// first.
    /// </summary>
    public static string? Write(string directory, Exception exception, string product, string version, bool enabled)
    {
        if (!enabled)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.txt");
            File.WriteAllText(path, Render(exception, product, version));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
