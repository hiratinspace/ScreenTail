using Microsoft.Extensions.Logging;
using ScreenTail.Shared.Logging;

namespace ScreenTail.Tests.Logging;

/// <summary>
/// ST-011: logs carry no content (INV-10). The messages are content-free by construction — the
/// service logs a title's length, never the title — and this is the belt to that brace: a scrubber
/// over every line, so a value that reaches a log by mistake reaches it as <c>[redacted]</c>, and a
/// path — which names the user, and sometimes the customer — as <c>[path]</c>.
/// </summary>
public sealed class ScrubberTests
{
    private static readonly Action<ILogger, string, string, Exception?> Foreground =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(1), "Foreground {Title} in {Process}");

    private static readonly Action<ILogger, Exception?> StoreFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2), "The store could not be opened");

    private static readonly Action<ILogger, int, int, Exception?> SceneFrame =
        LoggerMessage.Define<int, int>(LogLevel.Debug, new EventId(3), "Scene frame {Kept} of {Seen}");

    [Theory]
    [InlineData(@"Could not open C:\Users\hirat\AppData\Local\ScreenTail\store.db", "Could not open [path]")]
    [InlineData(@"Denied: \\fileserver\Customers\Acme Dental\notes.txt today", @"Denied: [path] today")]
    [InlineData("Wrote /Users/hirat/Downloads/Projects/ScreenTail/out.png", "Wrote [path]")]
    [InlineData("Wrote /home/tech/.local/share/screentail/store.db", "Wrote [path]")]
    [InlineData("Enrolled t.ortiz@acme-dental.example for the device", "Enrolled [email] for the device")]
    [InlineData("Retention removed raw data from 3 session(s)", "Retention removed raw data from 3 session(s)")]
    [InlineData("IPC contract v2, client verification strict", "IPC contract v2, client verification strict")]
    public void PathsAndAddressesAreScrubbedAndNothingElseIsTouched(string line, string expected)
    {
        Assert.Equal(expected, LogScrubber.Scrub(line));
    }

    [Theory]
    [InlineData("Title", true)]
    [InlineData("WindowTitle", true)]
    [InlineData("Text", true)]
    [InlineData("OcrText", true)]
    [InlineData("Transcript", true)]
    [InlineData("Note", true)]
    [InlineData("Clipboard", true)]
    [InlineData("Secret", true)]
    [InlineData("ApiKey", true)]
    [InlineData("Password", true)]
    [InlineData("Company", true)]
    [InlineData("Ticket", true)]
    [InlineData("TitleLength", false)]
    [InlineData("Hotkey", false)]
    [InlineData("Count", false)]
    [InlineData("Process", false)]
    [InlineData("Error", false)]
    public void TheKeysThatCarryContentAreKnownByName(string key, bool sensitive)
    {
        Assert.Equal(sensitive, LogScrubber.IsSensitiveKey(key));
    }

    [Fact]
    public void AStructuredValueUnderASensitiveKeyIsRedactedInTheLine()
    {
        using var sink = new StringWriter();
        var logger = new ScrubbingLoggerProvider(sink, LogLevel.Information).CreateLogger("ScreenTail.Test");

        Foreground(logger, "Acme Dental - Ticket 48213 - mstsc", "mstsc", null);

        var line = sink.ToString();
        Assert.Contains("[redacted]", line, StringComparison.Ordinal);
        Assert.Contains("in mstsc", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Acme", line, StringComparison.Ordinal);
        Assert.DoesNotContain("48213", line, StringComparison.Ordinal);
        Assert.Contains("INFO", line, StringComparison.Ordinal);
        Assert.Contains("ScreenTail.Test", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionIsWrittenWithItsMessageAndTraceScrubbed()
    {
        using var sink = new StringWriter();
        var logger = new ScrubbingLoggerProvider(sink, LogLevel.Information).CreateLogger("ScreenTail.Test");
        IOException? thrown = null;
        try
        {
            throw new IOException(@"Could not open C:\Users\hirat\AppData\Local\ScreenTail\store.db");
        }
        catch (IOException ex)
        {
            thrown = ex;
        }

        StoreFailed(logger, thrown);

        var line = sink.ToString();
        Assert.Contains("System.IO.IOException", line, StringComparison.Ordinal);
        Assert.Contains("Could not open [path]", line, StringComparison.Ordinal);
        Assert.DoesNotContain("hirat", line, StringComparison.Ordinal);
        Assert.Contains("ERROR", line, StringComparison.Ordinal);
    }

    [Fact]
    public void BelowTheMinimumNothingIsWritten()
    {
        using var sink = new StringWriter();
        var logger = new ScrubbingLoggerProvider(sink, LogLevel.Information).CreateLogger("ScreenTail.Test");

        SceneFrame(logger, 1, 2, null);

        Assert.Equal(string.Empty, sink.ToString());
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Information));
    }

    [Theory]
    [InlineData(null, LogLevel.Information)]
    [InlineData("", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("nonsense", LogLevel.Information)]
    public void TheLevelIsConfigurableAndInformationByDefault(string? value, LogLevel expected)
    {
        Assert.Equal(expected, LogLevels.Parse(value));
    }
}
