using Microsoft.AspNetCore.Http;
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

    // The hosting layer's request line, in its own order: a root path of "/" comes before a query that
    // has slashes in it.
    private static readonly Action<ILogger, string, string, Exception?> RequestLine =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(2), "Request starting HTTP/1.1 GET http://localhost{Path}{QueryString} - 401");

    // The same two values the other way round, so no order of keys is the lucky one.
    private static readonly Action<ILogger, string, string, Exception?> Reversed =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(3), "{QueryString} was asked of {Path}");

    // A value that overlaps another one where it appears, without either containing the other.
    private static readonly Action<ILogger, string, string, Exception?> Overlapping =
        LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(4), "{Note}: {Title} printer");

    // Values the message renders differently from their own ToString(): a format, and a list.
    private static readonly Action<ILogger, int, Exception?> Formatted =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(5), "Opened ticket {Ticket:X} for review");

    private static readonly Action<ILogger, string[], Exception?> Listed =
        LoggerMessage.Define<string[]>(LogLevel.Information, new EventId(6), "Searched for {Query} in 3 ms");

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
    [InlineData("redirect", true)]
    [InlineData("Location", true)]
    [InlineData("SourceValue", true)]
    [InlineData("@Company", true)]
    [InlineData("$Ticket", true)]
    [InlineData("@Elapsed", false)]
    [InlineData("Method", false)]
    [InlineData("StatusCode", false)]
    [InlineData("ElapsedMilliseconds", false)]
    public void TheFrameworksRequestKeysAreContentAndItsCountsAreNot(string key, bool sensitive)
    {
        // The hosting layer's request line and the HTTP client's call line, by the names they log under.
        Assert.Equal(sensitive, LogScrubber.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("CompanyName")]
    [InlineData("TicketNumber")]
    [InlineData("TicketId")]
    [InlineData("SessionNotes")]
    [InlineData("WindowTitles")]
    [InlineData("Companies")]
    [InlineData("Tickets")]
    [InlineData("Titles")]
    [InlineData("Texts")]
    [InlineData("Transcripts")]
    [InlineData("Emails")]
    [InlineData("Paths")]
    [InlineData("Files")]
    [InlineData("Urls")]
    [InlineData("ClientCompany")]
    [InlineData("LinkedTicket")]
    [InlineData("VaultSecrets")]
    [InlineData("AdminPasswords")]
    [InlineData("StoredCredentials")]
    [InlineData("RefreshTokens")]
    [InlineData("OcrTexts")]
    [InlineData("SegmentTranscripts")]
    [InlineData("@CompanyName")]
    public void TheClientsSensitiveNamesAreSensitiveHereToo(string key)
    {
        // The client's scrubber learned these in the 2026-10-12 review (T-116): a plural is the same content
        // as its singular, and a customer's company or ticket is content whatever word follows it. The
        // backend holds the same note and the same company for a request, so it refuses the same names.
        Assert.True(LogScrubber.IsSensitiveKey(key));
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

    [Fact]
    public void AValueInsideAnotherSensitiveValueDoesNotSaveIt()
    {
        using var sink = new StringWriter();
        var logger = new ScrubbingLoggerProvider(sink, LogLevel.Information).CreateLogger("ScreenTail.Api.Test");

        RequestLine(logger, "/", "?q=Acme/Dental", null);
        Reversed(logger, "?q=Acme/Dental", "/", null);
        RequestLine(logger, "/v1/psa/tickets", "?q=Acme/Dental/v1/psa/tickets", null);

        var log = sink.ToString();
        Assert.DoesNotContain("Acme", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Dental", log, StringComparison.Ordinal);
        Assert.Contains("GET http://localhost[redacted][redacted] - 401", log, StringComparison.Ordinal);
        Assert.Contains("[redacted] was asked of [redacted]", log, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesThatOverlapWhereTheyAppearAreBothRedacted()
    {
        using var sink = new StringWriter();
        var logger = new ScrubbingLoggerProvider(sink, LogLevel.Information).CreateLogger("ScreenTail.Api.Test");

        // "Dental printer: Acme Dental printer". Removing the note first used to cut the title in half.
        Overlapping(logger, "Dental printer", "Acme Dental", null);

        var line = sink.ToString();
        Assert.DoesNotContain("Acme", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Dental", line, StringComparison.Ordinal);
        Assert.Contains("[redacted]: [redacted]", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AValueTheMessageRendersDifferentlyIsStillRedacted()
    {
        using var sink = new StringWriter();
        var logger = new ScrubbingLoggerProvider(sink, LogLevel.Information).CreateLogger("ScreenTail.Api.Test");

        // 48213 is BC55 in hex, and a list is written as "Acme, Dental": neither is the value's ToString(),
        // so looking for the value in the finished line cannot find it. The line is built from its template.
        Formatted(logger, 48213, null);
        Listed(logger, ["Acme", "Dental"], null);

        var log = sink.ToString();
        Assert.DoesNotContain("BC55", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Acme", log, StringComparison.Ordinal);
        Assert.Contains("Opened ticket [redacted] for review", log, StringComparison.Ordinal);
        Assert.Contains("Searched for [redacted] in 3 ms", log, StringComparison.Ordinal);
    }

    [Fact]
    public void EverythingElseInTheTemplateIsWrittenAsTheFrameworkWouldWriteIt()
    {
        using var sink = new StringWriter();
        var logger = new ScrubbingLoggerProvider(sink, LogLevel.Information).CreateLogger("ScreenTail.Api.Test");

        // Escaped braces, an alignment, a format, a null, a list that is not content, and a name used twice.
        Shaped(logger, 3.14159, null, [200, 404], 7, 8, null);

        var line = sink.ToString();
        Assert.Contains("{literal} took [  3.1] for (null) with 200, 404 then 7 and 8", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutATemplateALineThatStillCarriesAValueIsWithheld()
    {
        using var sink = new StringWriter();
        var logger = new ScrubbingLoggerProvider(sink, LogLevel.Information).CreateLogger("ScreenTail.Api.Test");

        // A state with no template, so the finished line is all there is. The two values overlap in it.
        var overlapping = new Untemplated("Dental printer: Acme Dental printer", ("Note", "Dental printer"), ("Title", "Acme Dental"));
        var plain = new Untemplated("Opened 48213 after 12 ms", ("Ticket", "48213"), ("Elapsed", 12));
        logger.Log(LogLevel.Information, new EventId(7), overlapping, null, (state, _) => state.Message);
        logger.Log(LogLevel.Information, new EventId(8), plain, null, (state, _) => state.Message);

        var log = sink.ToString();
        Assert.DoesNotContain("Acme", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Dental", log, StringComparison.Ordinal);
        Assert.DoesNotContain("48213", log, StringComparison.Ordinal);
        Assert.Contains("Opened [redacted] after 12 ms", log, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadRequestsMessageIsNeverWrittenWhereverItIsLogged()
    {
        using var sink = new StringWriter();
        var logger = new ScrubbingLoggerProvider(sink, LogLevel.Information).CreateLogger("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware");

        // The message quotes the part of the request that could not be read, inner or outer.
        var bad = new BadHttpRequestException("Invalid request line: 'GET /v1/psa/tickets?q=Acme Dental HTTP/1.1'");
        Unhandled(logger, new InvalidOperationException("The request could not be handled.", bad));

        var log = sink.ToString();
        Assert.DoesNotContain("Acme", log, StringComparison.Ordinal);
        Assert.Contains("Microsoft.AspNetCore.Http.BadHttpRequestException: 400 [redacted]", log, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: The request could not be handled.", log, StringComparison.Ordinal);
    }

    [Fact]
    public void KestrelsBadRequestLinesKeepOnlyTheConnectionId()
    {
        using var sink = new StringWriter();
        var provider = new ScrubbingLoggerProvider(sink, LogLevel.Debug);
        var kestrel = provider.CreateLogger("Microsoft.AspNetCore.Server.Kestrel.BadRequests");
        var other = provider.CreateLogger("ScreenTail.Api.Test");

        BadRequestData(kestrel, "0HNPA86DOS3ED", "Invalid request header: 'Authorization : Bearer Acme-token'", null);
        var untemplated = new Untemplated("Rejected 'GET /?q=Acme'", ("Line", "GET /?q=Acme"));
        kestrel.Log(LogLevel.Debug, new EventId(17), untemplated, null, (state, _) => state.Message);
        kestrel.Log(LogLevel.Debug, new EventId(17), "Rejected 'GET /?q=Acme'", null, (state, _) => state);

        // The same names in any other category follow the ordinary rule.
        BadRequestData(other, "0HNPA86DOS3EE", "too many headers", null);

        var log = sink.ToString();
        Assert.DoesNotContain("Acme", log, StringComparison.Ordinal);
        Assert.Contains("Connection id \"0HNPA86DOS3ED\" bad request data: \"[redacted]\"", log, StringComparison.Ordinal);
        Assert.Contains("Microsoft.AspNetCore.Server.Kestrel.BadRequests: [redacted]" + Environment.NewLine, log, StringComparison.Ordinal);
        Assert.Contains("Connection id \"0HNPA86DOS3EE\" bad request data: \"too many headers\"", log, StringComparison.Ordinal);
    }

    // Kestrel's own template for a request it cannot parse (Kestrel spells the second name {message}).
    private static readonly Action<ILogger, string, string, Exception?> BadRequestData =
        LoggerMessage.Define<string, string>(LogLevel.Debug, new EventId(17), "Connection id \"{ConnectionId}\" bad request data: \"{Message}\"");

    private static readonly Action<ILogger, Exception?> Unhandled =
        LoggerMessage.Define(LogLevel.Error, new EventId(18), "An unhandled exception has occurred while executing the request.");

    private static readonly Action<ILogger, double, string?, int[], int, int, Exception?> Shaped =
        LoggerMessage.Define<double, string?, int[], int, int>(LogLevel.Information, new EventId(9), "{{literal}} took [{Elapsed,5:0.0}] for {Status} with {Codes} then {Count} and {Count}");

    private sealed class Untemplated(string message, params (string Key, object? Value)[] values) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public string Message { get; } = message;

        public int Count => values.Length;

        public KeyValuePair<string, object?> this[int index] => new(values[index].Key, values[index].Value);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var i = 0; i < values.Length; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
