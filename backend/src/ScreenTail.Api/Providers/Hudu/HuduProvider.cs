using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTail.Api.Providers.Hudu;

/// <summary>
/// Hudu's REST API, spoken as <see cref="IDocProvider"/> (ST-095, ST-096).
///
/// The tenant's key goes in the <c>x-api-key</c> header and never in a URL. Companies are paged at
/// Hudu's 25 and cached ten minutes per tenant. An article is created as a draft under its company, and
/// each attachment is uploaded against it afterwards, a few at a time, so a reviewer finds the
/// screenshots on the article whether or not the body's image links resolve. A company Hudu does not have is refused before
/// anything is sent: publishing to the wrong company is one customer's runbook in another's knowledge
/// base.
///
/// The base address is the tenant's site; every route below is under <c>api/v1/</c>.
/// </summary>
public sealed class HuduProvider(HttpClient http, string apiKey, HuduOptions options, HuduCompanyCache cache, string tenantKey, TimeProvider? time = null) : IDocProvider
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly string _apiKey = string.IsNullOrWhiteSpace(apiKey) ? throw new ArgumentException("An API key is required.", nameof(apiKey)) : apiKey;
    private readonly HuduOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly HuduCompanyCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly string _tenantKey = tenantKey ?? throw new ArgumentNullException(nameof(tenantKey));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => "hudu";

    public async Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default)
    {
        var answer = await SendAsync(() => Get("api/v1/api_info"), ct).ConfigureAwait(false);
        return answer.Ok ? ProviderResult.Success(true) : ProviderResult.Failure<bool>(answer.Error!);
    }

    public async Task<ProviderResult<IReadOnlyList<CompanyRef>>> ListCompaniesAsync(CancellationToken ct = default)
    {
        ProviderError? failed = null;
        // The site is the client's base address, which the factory set from the vault's row: a list from
        // any other site is not this one's.
        var companies = await _cache.GetOrAddAsync(_tenantKey, _http.BaseAddress?.AbsoluteUri ?? string.Empty, _options.CompanyCacheFor, async token =>
        {
            // Bounded twice over (2026-10-12 review): a site that answers every page in full would
            // otherwise be paged for ever on the shared backend, and the pages are counted as well as
            // the companies so a misconfigured page size cannot loop on empty ones.
            var size = Math.Max(1, _options.PageSize);
            var cap = Math.Max(0, _options.MaxCompanies);
            var all = new List<CompanyRef>();
            for (var page = 1; page <= (cap / size) + 1; page++)
            {
                var batch = ProviderHttp.Read(
                    await SendAsync(() => Get($"api/v1/companies?page={page}&page_size={size}"), token).ConfigureAwait(false),
                    Companies,
                    "Hudu",
                    written: false);
                if (!batch.Ok)
                {
                    failed = batch.Error;
                    return null;
                }

                all.AddRange(batch.Value!);
                if (all.Count > cap)
                {
                    break;
                }

                if (batch.Value!.Count < size)
                {
                    return all;
                }
            }

            // Not cached and not cut short: a list missing companies would refuse an article for one
            // that exists, and offer a mapping screen that cannot show it.
            failed = new ProviderError(
                ProviderErrorKind.Invalid,
                $"Hudu listed more than {cap} companies, which is more than ScreenTail reads.",
                "Check the site address in Settings → Integrations, or raise Hudu:MaxCompanies on the backend.");
            return null;
        }, ct).ConfigureAwait(false);

        return companies is null
            ? ProviderResult.Failure<IReadOnlyList<CompanyRef>>(failed ?? new ProviderError(ProviderErrorKind.Unavailable, "Hudu did not answer.", "Try again in a minute."))
            : ProviderResult.Success(companies);
    }

    public async Task<ProviderResult<PublishedArticle>> PublishArticleAsync(KbArticle article, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(article);
        var companies = await ListCompaniesAsync(ct).ConfigureAwait(false);
        if (!companies.Ok)
        {
            return ProviderResult.Failure<PublishedArticle>(companies.Error!);
        }

        if (!int.TryParse(article.CompanyId, NumberStyles.None, CultureInfo.InvariantCulture, out var companyId)
            || !companies.Value!.Any(c => c.Id == article.CompanyId))
        {
            return ProviderResult.Failure<PublishedArticle>(new ProviderError(
                ProviderErrorKind.NotFound,
                $"Company {article.CompanyId} is not in Hudu.",
                "Map the company in Settings → Integrations."));
        }

        var body = new
        {
            article = new
            {
                name = article.Title,
                content = article.Body,
                company_id = companyId,
                draft = article.Draft,
            },
        };
        var created = ProviderHttp.Read(await SendAsync(() => Post("api/v1/articles", body), ct).ConfigureAwait(false), Created, "Hudu", written: true);
        if (!created.Ok)
        {
            // Hudu said the company must exist: the cached list named one it has since deleted. The list
            // is out of date, so it goes, and the next lookup (the mapping prompt's among them) asks
            // Hudu. Only on that answer: a plain 404 says nothing about the company (review finding 1).
            if (created.Error!.CompanyGone)
            {
                _cache.Forget(_tenantKey);
            }

            return ProviderResult.Failure<PublishedArticle>(created.Error!);
        }

        var (id, url) = created.Value!;

        // The article exists from here on: a screenshot that does not attach is named, not a failure,
        // because a failure is what Retry sends again and that would be a second article (S-69).
        var missing = await AttachmentUploads.UploadAsync(
            article.Attachments,
            (attachment, token) => SendAsync(() => Upload(id, attachment), token),
            $"The article was published as {id}",
            "The article stays as it is; add the missing screenshots to it by hand rather than publishing it again.",
            ct).ConfigureAwait(false);

        return ProviderResult.Success(new PublishedArticle(id, url, missing));
    }

    /// <summary>
    /// One page of companies. The list must be there: a page without it (an empty body, or JSON from
    /// something that is not Hudu's API) throws for <see cref="ProviderHttp.Read{T}"/> to report, rather
    /// than reading as a tenant with no companies and being remembered as one. An empty list is a list.
    /// </summary>
    private static List<CompanyRef> Companies(JsonElement page) =>
        [.. page.GetProperty("companies").EnumerateArray().Select(c => new CompanyRef(ProviderHttp.Id(c), c.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty))];

    /// <summary>The new article's id and link. Hudu wraps it in <c>article</c>; a bare object is read the same way.</summary>
    private static (string Id, Uri? Url) Created(JsonElement answer)
    {
        var element = answer.TryGetProperty("article", out var inner) ? inner : answer;
        var url = element.TryGetProperty("url", out var link) && link.ValueKind == JsonValueKind.String && Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri) ? uri : null;
        return (ProviderHttp.Id(element), url);
    }

    private HttpRequestMessage Get(string route) => Prepare(new HttpRequestMessage(HttpMethod.Get, route));

    private HttpRequestMessage Post(string route, object body) =>
        Prepare(new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: Wire) });

    private HttpRequestMessage Upload(string articleId, NoteAttachment attachment)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("Article"), "uploadable_type" },
            { new StringContent(articleId), "uploadable_id" },
        };
        var file = new ReadOnlyMemoryContent(attachment.Bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
        form.Add(file, "file", attachment.FileName);
        return Prepare(new HttpRequestMessage(HttpMethod.Post, "api/v1/uploads") { Content = form });
    }

    private HttpRequestMessage Prepare(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private Task<ProviderResult<JsonElement>> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct) =>
        ProviderHttp.SendAsync(
            _http,
            build,
            new RetryPolicy(_options.BackoffBase, _options.MaxAttempts) { MaxWait = _options.MaxRetryWait },
            (status, body, retryAfter) => Failure(status, body, ProviderHttp.RetryAfter(retryAfter, _time)),
            "Hudu",
            _time,
            ct);

    private static ProviderError Failure(HttpStatusCode status, string body, TimeSpan? retryAfter) => status switch
    {
        HttpStatusCode.Unauthorized => new ProviderError(ProviderErrorKind.Unauthenticated, "Hudu rejected the API key.", "Update it in Settings → Integrations."),
        HttpStatusCode.Forbidden => new ProviderError(ProviderErrorKind.Forbidden, "Hudu says this API key may not do that.", "Ask your Hudu administrator for the permission."),
        HttpStatusCode.NotFound => new ProviderError(ProviderErrorKind.NotFound, "Hudu has no such company or article.", "Pick a different one."),
        HttpStatusCode.TooManyRequests => new ProviderError(ProviderErrorKind.Unavailable, "Hudu is rate-limiting requests.", ProviderHttp.TryAgain(retryAfter), retryAfter),
        >= HttpStatusCode.InternalServerError => new ProviderError(ProviderErrorKind.Unavailable, "Hudu did not answer.", ProviderHttp.TryAgain(retryAfter), retryAfter),

        // An article names its company, and Hudu refuses one it no longer has. ScreenTail checked the
        // company against its cached list, so this is a company deleted since: another company is the
        // answer, not a bug report.
        HttpStatusCode.UnprocessableEntity when CompanyGone(body) => new ProviderError(ProviderErrorKind.NotFound, "Hudu no longer has that company.", "Choose another company for the article, then publish it again.") { CompanyGone = true },
        _ => new ProviderError(ProviderErrorKind.Invalid, $"Hudu refused the request: {Reason(body)}.", "This is a ScreenTail bug; there is nothing to change on your side."),
    };

    /// <summary>Hudu's 422 for a company it does not have: <c>{"errors":{"company":["must exist"]}}</c>, or the same as one sentence.</summary>
    private static bool CompanyGone(string body) => Reason(body).ToUpperInvariant() is "COMPANY MUST EXIST" or "COMPANY_ID MUST EXIST";

    /// <summary>Hudu's 422 lists errors by field: <c>{"errors":{"name":["can't be blank"]}}</c>. The first is the reason.</summary>
    private static string Reason(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("errors", out var errors))
            {
                if (errors.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in errors.EnumerateObject())
                    {
                        var first = field.Value.ValueKind == JsonValueKind.Array ? field.Value.EnumerateArray().FirstOrDefault().GetString() : field.Value.GetString();
                        return ProviderHttp.Sanitise($"{field.Name} {first}");
                    }
                }
                else if (errors.ValueKind == JsonValueKind.String)
                {
                    return ProviderHttp.Sanitise(errors.GetString());
                }
            }

            return ProviderHttp.Sanitise(root.TryGetProperty("error", out var error) ? error.GetString() : null);
        }
        catch (JsonException)
        {
            return "no reason given";
        }
    }
}
