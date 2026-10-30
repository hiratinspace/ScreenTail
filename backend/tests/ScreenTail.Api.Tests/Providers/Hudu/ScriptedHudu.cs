using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ScreenTail.Api.Providers;

namespace ScreenTail.Api.Tests.Providers.Hudu;

/// <summary>
/// A Hudu that answers from recorded shapes (ST-095). Routes are the real ones under <c>/api/v1</c>; the
/// bodies carry the fields the provider reads. Twenty-eight companies over two pages, so pagination is
/// exercised; a forced failure stands in for the outage and the revoked key.
/// </summary>
internal sealed class ScriptedHudu : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] MustExist = ["must exist"];

    private readonly Lock _gate = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private readonly List<string?> _bodies = [];
    private readonly TaskCompletionSource _hung = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What was asked, in arrival order; a copy taken under the lock, because uploads go several at a time.</summary>
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

    public Queue<HttpResponseMessage> Scripted { get; } = new();

    public HttpStatusCode? Failing { get; set; }

    /// <summary>How many companies the site has. Twenty-eight by default; as many as an int holds for a site that never ends.</summary>
    public int CompanyCount { get; set; } = 28;

    /// <summary>One route fails with this status while set: the article is created and an upload is refused.</summary>
    public (string PathSuffix, HttpStatusCode Status)? FailingRoute { get; set; }

    /// <summary>Requests to a path ending in this are never answered: they wait until the caller gives up.</summary>
    public string? HangingRoute { get; set; }

    /// <summary>Done once a request to <see cref="HangingRoute"/> has arrived and is waiting: what a test waits for before letting a deadline end it.</summary>
    public Task Hung => _hung.Task;

    /// <summary>
    /// The token the hung request was sent with, once <see cref="Hung"/> is done. Cancelled the moment
    /// the deadline fires, on the thread that fired it, so a test reads exactly when the deadline came
    /// rather than when the answer it causes has made its way back.
    /// </summary>
    public CancellationToken HungOn { get; private set; }

    /// <summary>Requests to a path ending in this are answered 200 with this body, which is not what the route returns.</summary>
    public (string PathSuffix, string Body)? UnreadableRoute { get; set; }

    /// <summary>
    /// The name a site gives a company id, when it is not the recorded one. Two Hudu sites number their
    /// companies independently, so the same id can be a different customer on each (S-71).
    /// </summary>
    public Func<string, int, string?>? RenamedAt { get; set; }

    /// <summary>
    /// Companies deleted on the site: no longer listed, and an article filed under one is refused with
    /// Hudu's 422 that the company must exist. Added to after a list was cached, it is the company
    /// removed in Hudu while the backend still remembers it.
    /// </summary>
    public HashSet<int> Removed { get; } = [];

    public static HttpStatusCode StatusFor(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.Unauthenticated => HttpStatusCode.Unauthorized,
        ProviderErrorKind.Forbidden => HttpStatusCode.Forbidden,
        ProviderErrorKind.NotFound => HttpStatusCode.NotFound,
        ProviderErrorKind.Invalid => HttpStatusCode.UnprocessableEntity,
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
            // A provider that pages without end would hang the run; this ends it with the reason instead.
            if (_requests.Count >= 1_000)
            {
                throw new InvalidOperationException("The provider kept asking: a thousand requests to one scripted Hudu.");
            }

            _requests.Add(request);
            _bodies.Add(body);
            if (Scripted.Count > 0)
            {
                return Scripted.Dequeue();
            }
        }

        var path = request.RequestUri!.AbsolutePath;
        if (HangingRoute is { } hanging && path.EndsWith(hanging, StringComparison.Ordinal))
        {
            var waiting = Task.Delay(Timeout.Infinite, cancellationToken);
            HungOn = cancellationToken;
            _ = _hung.TrySetResult();
            await waiting;
        }

        if (Failing is { } status)
        {
            return new HttpResponseMessage(status) { Content = JsonContent.Create(new { error = "The script said no" }, options: Json) };
        }

        if (UnreadableRoute is { } unreadable && path.EndsWith(unreadable.PathSuffix, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(unreadable.Body) };
        }

        if (FailingRoute is { } route && path.EndsWith(route.PathSuffix, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(route.Status) { Content = JsonContent.Create(new { error = "The script said no" }, options: Json) };
        }

        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        return (request.Method.Method, path) switch
        {
            ("GET", var p) when p.EndsWith("/api/v1/api_info", StringComparison.Ordinal) =>
                Ok(new { version = "2.27.1", date = "2026-09-01" }),

            ("GET", var p) when p.EndsWith("/api/v1/companies", StringComparison.Ordinal) =>
                Ok(new { companies = Companies(request.RequestUri.Host, int.Parse(query["page"] ?? "1", System.Globalization.CultureInfo.InvariantCulture), int.Parse(query["page_size"] ?? "25", System.Globalization.CultureInfo.InvariantCulture)) }),

            ("POST", var p) when p.EndsWith("/api/v1/articles", StringComparison.Ordinal) && Removed.Contains(JsonDocument.Parse(body!).RootElement.GetProperty("article").GetProperty("company_id").GetInt32()) =>
                new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = JsonContent.Create(new { errors = new { company = MustExist } }, options: Json) },

            ("POST", var p) when p.EndsWith("/api/v1/articles", StringComparison.Ordinal) =>
                Ok(new { article = new { id = 5101, name = "Printer offline", url = "https://acme.huducloud.com/a/printer-offline-5101", draft = true, company_id = 7 } }),

            ("POST", var p) when p.EndsWith("/api/v1/uploads", StringComparison.Ordinal) =>
                Ok(new { upload = new { id = 9001, uploadable_type = "Article", uploadable_id = 5101 } }),

            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private object[] Companies(string host, int page, int pageSize)
    {
        if (Removed.Count > 0)
        {
            // The site's own paging, over what is left: a deleted company leaves no hole in a page.
            return [.. Enumerable.Range(1, CompanyCount).Where(i => !Removed.Contains(i)).Skip((page - 1) * pageSize).Take(pageSize).Select(i => Company(host, i))];
        }

        var first = ((long)page - 1) * pageSize + 1;
        var last = Math.Min(CompanyCount, first + pageSize - 1);
        return [.. Enumerable.Range(0, (int)Math.Max(0, last - first + 1)).Select(offset => (int)(first + offset)).Select(i => Company(host, i))];
    }

    private object Company(string host, int i) => new { id = i, name = RenamedAt?.Invoke(host, i) ?? (i == 7 ? "Acme Dental" : i == 8 ? "Borough Legal" : i == 9 ? "Bright Smiles" : $"Company {i}") };

    private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Json) };
}
