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

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string?> Bodies { get; } = [];

    public Queue<HttpResponseMessage> Scripted { get; } = new();

    public HttpStatusCode? Failing { get; set; }

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
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        if (Scripted.Count > 0)
        {
            return Scripted.Dequeue();
        }

        if (Failing is { } status)
        {
            return new HttpResponseMessage(status) { Content = JsonContent.Create(new { error = "The script said no" }, options: Json) };
        }

        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        return (request.Method.Method, path) switch
        {
            ("GET", var p) when p.EndsWith("/api/v1/api_info", StringComparison.Ordinal) =>
                Ok(new { version = "2.27.1", date = "2026-09-01" }),

            ("GET", var p) when p.EndsWith("/api/v1/companies", StringComparison.Ordinal) =>
                Ok(new { companies = Companies(int.Parse(query["page"] ?? "1", System.Globalization.CultureInfo.InvariantCulture), int.Parse(query["page_size"] ?? "25", System.Globalization.CultureInfo.InvariantCulture)) }),

            ("POST", var p) when p.EndsWith("/api/v1/articles", StringComparison.Ordinal) =>
                Ok(new { article = new { id = 5101, name = "Printer offline", url = "https://acme.huducloud.com/a/printer-offline-5101", draft = true, company_id = 7 } }),

            ("POST", var p) when p.EndsWith("/api/v1/uploads", StringComparison.Ordinal) =>
                Ok(new { upload = new { id = 9001, uploadable_type = "Article", uploadable_id = 5101 } }),

            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private static object[] Companies(int page, int pageSize)
    {
        var all = Enumerable.Range(1, 28).Select(i => new { id = i, name = i == 7 ? "Acme Dental" : i == 8 ? "Borough Legal" : $"Company {i}" }).ToList();
        return [.. all.Skip((page - 1) * pageSize).Take(pageSize)];
    }

    private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Json) };
}
