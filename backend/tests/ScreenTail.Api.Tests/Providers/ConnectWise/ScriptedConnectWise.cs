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
    private readonly TaskCompletionSource _hung = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>The Retry-After the failing route's answer carries, when set: the PSA that names its wait.</summary>
    public TimeSpan? FailingRouteRetryAfter { get; set; }

    /// <summary>Requests to a path ending in this are never answered: they wait until the caller gives up. The PSA that is too slow for the deadline.</summary>
    public string? HangingRoute { get; set; }

    /// <summary>Done once a request to <see cref="HangingRoute"/> has arrived and is waiting: what a test waits for before letting a deadline end it.</summary>
    public Task Hung => _hung.Task;

    /// <summary>
    /// The token the hung request was sent with, once <see cref="Hung"/> is done. Cancelled the moment
    /// the deadline fires, on the thread that fired it, so a test reads exactly when the deadline came
    /// rather than when the answer it causes has made its way back.
    /// </summary>
    public CancellationToken HungOn { get; private set; }

    /// <summary>Requests to a path ending in this are answered 200 with this body, which is not what the route returns: the PSA that took the request and sent back something that cannot be read.</summary>
    public (string PathSuffix, string Body)? UnreadableRoute { get; set; }

    /// <summary>
    /// The tenant's companies, by id, for the company lookup: the names the tickets carry and the ones the
    /// mapping tests choose, and one with a quote in it.
    /// </summary>
    public List<(int Id, string Name)> Companies { get; } =
    [
        (7, "Acme Dental"),
        (8, "Borough Legal"),
        (9, "Bright Smiles"),
        (10, "Nowhere Inc"),
        (11, "Beta Law"),
        (12, "O'Brien & Sons"),
        (13, "Smith and O'Brien"),
        (14, "Smith and Sons"),
    ];

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
            var waiting = Task.Delay(Timeout.Infinite, cancellationToken);
            HungOn = cancellationToken;
            _ = _hung.TrySetResult();
            await waiting;
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
            var refused = new HttpResponseMessage(route.Status) { Content = JsonContent.Create(new { code = "Forced", message = "The script said no" }, options: Json) };
            if (FailingRouteRetryAfter is { } wait)
            {
                refused.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
            }

            return refused;
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

            ("GET", var p) when p.EndsWith("/company/companies", StringComparison.Ordinal) =>
                ListCompanies(query),

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

    /// <summary>
    /// <c>name = '…'</c> or <c>name contains '…'</c> clauses joined by <c>and</c> outside quotes, compared
    /// without regard to case as ConnectWise's database compares them, one page of <c>pageSize</c> (25
    /// unless said). Any other condition, or a quote inside a value, is a field error.
    /// </summary>
    private HttpResponseMessage ListCompanies(string query)
    {
        var parameters = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty, StringComparer.Ordinal);
        IEnumerable<(int Id, string Name)> matched = Companies;
        foreach (var clause in Clauses(parameters.GetValueOrDefault("conditions") ?? string.Empty))
        {
            var (op, value) = clause.StartsWith("name = '", StringComparison.Ordinal) ? ("=", clause["name = '".Length..])
                : clause.StartsWith("name contains '", StringComparison.Ordinal) ? ("contains", clause["name contains '".Length..])
                : (null, string.Empty);
            if (op is null || !value.EndsWith('\'') || value[..^1].Contains('\''))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { code = "InvalidObject", message = $"The condition {clause} could not be parsed." }, options: Json) };
            }

            var wanted = value[..^1];
            matched = op == "="
                ? matched.Where(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase))
                : matched.Where(c => c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        }

        var size = parameters.TryGetValue("pageSize", out var given) ? int.Parse(given, System.Globalization.CultureInfo.InvariantCulture) : 25;
        return Ok(matched.Take(size).Select(c => new { id = c.Id, name = c.Name }).ToArray());
    }

    /// <summary>A conditions string split on <c>and</c> where it stands outside a quoted value, as ConnectWise's parser reads it.</summary>
    internal static List<string> Clauses(string conditions)
    {
        var clauses = new List<string>();
        var quoted = false;
        var start = 0;
        for (var i = 0; i < conditions.Length; i++)
        {
            if (conditions[i] == '\'')
            {
                quoted = !quoted;
            }
            else if (!quoted && string.CompareOrdinal(conditions, i, " and ", 0, 5) == 0)
            {
                clauses.Add(conditions[start..i].Trim());
                start = i + 5;
                i += 4;
            }
        }

        clauses.Add(conditions[start..].Trim());
        return clauses.FindAll(c => c.Length > 0);
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
