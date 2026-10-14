using Microsoft.Extensions.Logging;
using ScreenTail.Api.Logging;

namespace ScreenTail.Api.Tests.Logging;

/// <summary>
/// ST-011 on the backend: the same scrubber as the client's, over every line the API logs. The API
/// holds a note's text and a ticket's company in memory for one request (INV-7); neither may land in
/// a log because a framework message included a value.
/// </summary>
public sealed class LogScrubberTests
{
    private static readonly Action<ILogger, string, string, int, Exception?> Published =
        LoggerMessage.Define<string, string, int>(LogLevel.Information, new EventId(1), "Published to {Company} ticket {Ticket} in {Elapsed} ms");

    [Theory]
    [InlineData("Wrote /home/app/.aspnet/DataProtection-Keys/key.xml", "Wrote [path]")]
    [InlineData(@"Denied C:\inetpub\screentail\appsettings.json", "Denied [path]")]
    [InlineData("Invited t.ortiz@acme-dental.example", "Invited [email]")]
    [InlineData("Request finished HTTP/1.1 POST /v1/sessions/publish - 200", "Request finished HTTP/1.1 POST /v1/sessions/publish - 200")]
    public void PathsAndAddressesAreScrubbedAndRoutesAreNot(string line, string expected)
    {
        Assert.Equal(expected, LogScrubber.Scrub(line));
    }

    [Theory]
    [InlineData("QueryString", true)]
    [InlineData("Path", true)]
    [InlineData("Uri", true)]
    [InlineData("RequestUri", true)]
    [InlineData("BaseUrl", true)]
    [InlineData("SearchQuery", true)]
    [InlineData("Method", false)]
    [InlineData("StatusCode", false)]
    [InlineData("ElapsedMilliseconds", false)]
    public void TheFrameworksRequestKeysAreContentAndItsCountsAreNot(string key, bool sensitive)
    {
        // The hosting layer's request line and the HTTP client's call line, by the names they log under.
        Assert.Equal(sensitive, LogScrubber.IsSensitiveKey(key));
    }

    [Fact]
    public void AValueUnderASensitiveKeyIsRedacted()
    {
        using var sink = new StringWriter();
        var logger = new ScrubbingLoggerProvider(sink, LogLevel.Information).CreateLogger("ScreenTail.Api.Test");

        Published(logger, "Acme Dental", "48213", 812, null);

        var line = sink.ToString();
        Assert.DoesNotContain("Acme", line, StringComparison.Ordinal);
        Assert.DoesNotContain("48213", line, StringComparison.Ordinal);
        Assert.Contains("[redacted]", line, StringComparison.Ordinal);
        Assert.Contains("812 ms", line, StringComparison.Ordinal);
    }
}
