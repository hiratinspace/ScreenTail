using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Tests.Providers.ConnectWise;

/// <summary>
/// A ConnectWise Manage that answers from recorded shapes (ST-091 AC3). Routes are the real ones; the
/// bodies are the fields the provider reads, in the JSON the API returns them in. A forced failure
/// stands in for the outage, the revoked key and the rest, so the contract harness can break it.
/// </summary>
internal sealed class ScriptedConnectWise : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string?> _bodies = [];

    /// <summary>
    /// What was asked, in arrival order. A copy, taken under the lock: the publisher sends its
    /// destinations and attachments at the same time, so the lists are written from several threads.
    /// </summary>
    public List<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Each request's body, at the same index as <see cref="Requests"/>.</summary>
    public List<string?> Bodies
    {
        get
        {
            lock (_gate)
            {
                return [.. _bodies];
            }
        }
    }

    /// <summary>Answers in order for the next requests, before the routes are consulted. For backoff tests.</summary>
    public Queue<HttpResponseMessage> Scripted { get; } = new();

    /// <summary>Every request fails with this status while set. The contract's <c>Break</c>.</summary>
    public HttpStatusCode? Failing { get; set; }

    /// <summary>One route fails with this status while set: the partial publish, where the note lands and the time entry does not.</summary>
    public (string PathSuffix, HttpStatusCode Status)? FailingRoute { get; set; }

    /// <summary>Requests to a path ending in this are never answered: they wait until the caller gives up. The PSA that is too slow for the deadline.</summary>
    public string? HangingRoute { get; set; }

    /// <summary>Requests to a path ending in this are answered 200 with this body, which is not what the route returns: the PSA that took the request and sent back something that cannot be read.</summary>
    public (string PathSuffix, string Body)? UnreadableRoute { get; set; }

    public static HttpStatusCode StatusFor(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.Unauthenticated => HttpStatusCode.Unauthorized,
        ProviderErrorKind.Forbidden => HttpStatusCode.Forbidden,
        ProviderErrorKind.NotFound => HttpStatusCode.NotFound,
        ProviderErrorKind.Invalid => HttpStatusCode.BadRequest,
        _ => HttpStatusCode.ServiceUnavailable,
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // A real handler refuses a request whose caller has already given up; so does this one, or a
        // call made after the publish deadline would land here and nowhere else.
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            _requests.Add(request);
            _bodies.Add(body);
            if (Scripted.Count > 0)
            {
                return Scripted.Dequeue();
            }
        }

        if (HangingRoute is { } hanging && request.RequestUri!.AbsolutePath.EndsWith(hanging, StringComparison.Ordinal))
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Failing is { } status)
        {
            return new HttpResponseMessage(status) { Content = JsonContent.Create(new { code = "Forced", message = "The script said no" }, options: Json) };
        }

        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        if (UnreadableRoute is { } unreadable && path.EndsWith(unreadable.PathSuffix, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(unreadable.Body) };
        }

        if (FailingRoute is { } route && path.EndsWith(route.PathSuffix, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(route.Status) { Content = JsonContent.Create(new { code = "Forced", message = "The script said no" }, options: Json) };
        }

        return (request.Method.Method, path) switch
        {
            ("GET", var p) when p.EndsWith("/system/info", StringComparison.Ordinal) =>
                Ok(new { version = "v2026.1", isCloud = true, serverTimeZone = "Eastern Standard Time" }),

            ("GET", var p) when p.EndsWith("/service/tickets/48213", StringComparison.Ordinal) =>
                Ok(Ticket(48213, "Printer offline in reception", "Acme Dental", "In Progress")),

            ("GET", var p) when p.Contains("/service/tickets/", StringComparison.Ordinal) =>
                NotFound("Ticket not found"),

            // A summary search answers for "printer" and nothing else; a listing with no summary
            // condition (the recent tickets) answers with the same two.
            ("GET", var p) when p.EndsWith("/service/tickets", StringComparison.Ordinal) =>
                Ok(query.Contains("printer", StringComparison.OrdinalIgnoreCase) || !Uri.UnescapeDataString(query).Contains("summary contains", StringComparison.OrdinalIgnoreCase)
                    ? new[]
                    {
                        Ticket(48213, "Printer offline in reception", "Acme Dental", "In Progress"),
                        Ticket(48190, "Printer driver on the new laptop", "Bright Smiles", "New"),
                    }
                    : []),

            ("POST", var p) when p.EndsWith("/service/tickets/48213/notes", StringComparison.Ordinal) =>
                Ok(new { id = 90001, ticketId = 48213, text = "…" }),

            ("POST", var p) when p.Contains("/service/tickets/", StringComparison.Ordinal) && p.EndsWith("/notes", StringComparison.Ordinal) =>
                NotFound("Ticket not found"),

            ("POST", var p) when p.EndsWith("/time/entries", StringComparison.Ordinal) =>
                Ok(new { id = 70001, chargeToId = 48213, chargeToType = "ServiceTicket" }),

            ("POST", var p) when p.EndsWith("/system/documents", StringComparison.Ordinal) =>
                Ok(new { id = 55001, title = "frame" }),

            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static object Ticket(int id, string summary, string company, string status) => new
    {
        id,
        summary,
        company = new { id = 7, identifier = company.ToUpperInvariant().Replace(' ', '_'), name = company },
        status = new { id = 3, name = status },
        dateEntered = "2026-09-24T14:02:00Z",
    };

    private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Json) };

    private static HttpResponseMessage NotFound(string message) => new(HttpStatusCode.NotFound)
    {
        Content = JsonContent.Create(new { code = "NotFound", message }, options: Json),
    };
}
