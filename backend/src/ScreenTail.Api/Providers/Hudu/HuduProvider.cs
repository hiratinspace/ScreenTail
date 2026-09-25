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
/// each attachment is uploaded against it afterwards, so a reviewer finds the screenshots on the article
/// whether or not the body's image links resolve. A company Hudu does not have is refused before
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
        var companies = await _cache.GetOrAddAsync(_tenantKey, _options.CompanyCacheFor, async token =>
        {
            var all = new List<CompanyRef>();
            for (var page = 1; ; page++)
            {
                var answer = await SendAsync(() => Get($"api/v1/companies?page={page}&page_size={_options.PageSize}"), token).ConfigureAwait(false);
                if (!answer.Ok)
                {
                    failed = answer.Error;
                    return null;
                }

                var batch = answer.Value!.TryGetProperty("companies", out var list) ? list.EnumerateArray().ToList() : [];
                all.AddRange(batch.Select(c => new CompanyRef(c.GetProperty("id").GetRawText(), c.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty)));
                if (batch.Count < _options.PageSize)
                {
                    return all;
                }
            }
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
        var created = await SendAsync(() => Post("api/v1/articles", body), ct).ConfigureAwait(false);
        if (!created.Ok)
        {
            return ProviderResult.Failure<PublishedArticle>(created.Error!);
        }

        var element = created.Value!.TryGetProperty("article", out var inner) ? inner : created.Value!;
        var id = element.GetProperty("id").GetRawText();
        var url = element.TryGetProperty("url", out var link) && Uri.TryCreate(link.GetString(), UriKind.Absolute, out var uri) ? uri : null;

        var attached = 0;
        foreach (var attachment in article.Attachments)
        {
            var upload = await SendAsync(() => Upload(id, attachment), ct).ConfigureAwait(false);
            if (!upload.Ok)
            {
                return ProviderResult.Failure<PublishedArticle>(upload.Error! with
                {
                    What = $"The article was created as {id}, but attachment {attached + 1} of {article.Attachments.Count} was refused: {upload.Error.What}",
                });
            }

            attached++;
        }

        return ProviderResult.Success(new PublishedArticle(id, url));
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
        var file = new ByteArrayContent(attachment.Bytes.ToArray());
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
            new RetryPolicy(_options.BackoffBase, _options.MaxAttempts),
            (status, body, retryAfter) => Failure(status, body, ProviderHttp.RetryAfter(retryAfter, _time)),
            "Hudu",
            _time,
            ct);

    private static ProviderError Failure(HttpStatusCode status, string body, TimeSpan? retryAfter) => status switch
    {
        HttpStatusCode.Unauthorized => new ProviderError(ProviderErrorKind.Unauthenticated, "Hudu rejected the API key.", "Update it in Settings → Integrations."),
        HttpStatusCode.Forbidden => new ProviderError(ProviderErrorKind.Forbidden, "Hudu says this API key may not do that.", "Ask your Hudu administrator for the permission."),
        HttpStatusCode.NotFound => new ProviderError(ProviderErrorKind.NotFound, "Hudu has no such company or article.", "Pick a different one."),
        HttpStatusCode.TooManyRequests => new ProviderError(ProviderErrorKind.Unavailable, "Hudu is rate-limiting requests.", "Try again in a minute.", retryAfter),
        >= HttpStatusCode.InternalServerError => new ProviderError(ProviderErrorKind.Unavailable, "Hudu did not answer.", "Try again in a minute.", retryAfter),
        _ => new ProviderError(ProviderErrorKind.Invalid, $"Hudu refused the request: {Reason(body)}.", "This is a ScreenTail bug; there is nothing to change on your side."),
    };

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
