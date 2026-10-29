using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenTail.Api.Tests.Providers.StandIns;

internal sealed record HuduCompany(int Id, string Name);

/// <summary>An article the stand-in took, as Hudu would keep it.</summary>
internal sealed record HuduArticle(int Id, string Name, string Content, int CompanyId, bool Draft, Uri Url);

/// <summary>An upload the stand-in took against an article.</summary>
internal sealed record HuduUpload(int Id, string UploadableType, int UploadableId, string FileName, string ContentType, long Length);

/// <summary>
/// A stand-in Hudu for the provider tests (ST-095 to ST-097): the routes <c>HuduProvider</c> calls,
/// under <c>/api/v1/</c>, answered over HTTPS from state it keeps, in the shapes the recorded fixtures
/// (<c>ScriptedHudu</c>) already capture.
///
/// What it holds the provider to: the key in <c>x-api-key</c> and nowhere else (401 otherwise, and a key
/// in the query string is a 401 too, because it is a key in a log somewhere); companies paged by
/// <c>page</c> and <c>page_size</c>, twenty-eight of them by default so the list takes two pages; an
/// article wrapped in <c>article</c> with a name, its content and a company that exists, refused by field
/// in a 422 (<c>{"errors": {"field": ["message"]}}</c>) otherwise; an upload that names an article that
/// exists and carries a file.
///
/// What it is not: Hudu. The routes and field names are the provider's; the rules are the stand-in's
/// reading of the API reference. Whether Hudu honours <c>draft</c>, and the field names it wants for an
/// upload, are for the real check in <c>docs/integrations/hudu.md</c>.
/// </summary>
internal sealed partial class HuduStandIn : StandInHost
{
    private readonly List<HuduCompany> _companies = [];
    private readonly List<HuduArticle> _articles = [];
    private readonly List<HuduUpload> _uploads = [];
    private int _nextArticle = 5101;
    private int _nextUpload = 9001;

    public HuduStandIn()
    {
        for (var id = 1; id <= 28; id++)
        {
            _companies.Add(new HuduCompany(id, id switch
            {
                7 => "Acme Dental",
                8 => "Borough Legal",
                9 => "Bright Smiles",
                27 => "Harbour Physiotherapy",
                _ => $"Company {id}",
            }));
        }
    }

    public string ApiKey { get; } = RandomToken(12);

    public IReadOnlyList<HuduCompany> Companies => Locked(() => _companies.ToList());

    public IReadOnlyList<HuduArticle> Articles => Locked(() => _articles.ToList());

    public IReadOnlyList<HuduUpload> Uploads => Locked(() => _uploads.ToList());

    /// <summary>A company removed in Hudu, say by an administrator tidying up, while a list with it in is still cached.</summary>
    public void RemoveCompany(int id) => _ = Locked(() => _companies.RemoveAll(c => c.Id == id));

    protected override Reply? Authenticate(RecordedRequest request)
    {
        var inQuery = request.Query.Keys.Any(k => k.Contains("key", StringComparison.OrdinalIgnoreCase));
        return !inQuery && string.Equals(request.Header("x-api-key"), ApiKey, StringComparison.Ordinal)
            ? null
            : new Reply(401, new { error = "Invalid API key" });
    }

    protected override Reply Answer(RecordedRequest request) => (request.Method, request.Path) switch
    {
        ("GET", "/api/v1/api_info") => new Reply(200, new { version = "2.27.1", date = "2026-09-01" }),
        ("GET", "/api/v1/companies") => ListCompanies(request),
        ("POST", "/api/v1/articles") => AddArticle(request),
        ("POST", "/api/v1/uploads") => AddUpload(request),
        _ => new Reply(404, new { error = "Not found" }),
    };

    public static Reply FieldError(string field, string message) => new(422, new Dictionary<string, object>
    {
        ["errors"] = new Dictionary<string, string[]> { [field] = [message] },
    });

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    private Reply ListCompanies(RecordedRequest request)
    {
        var page = request.Query.TryGetValue("page", out var p) ? int.Parse(p, CultureInfo.InvariantCulture) : 1;
        var size = request.Query.TryGetValue("page_size", out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : 25;
        if (page < 1 || size < 1)
        {
            return FieldError("page", "must be at least 1");
        }

        var rows = _companies.OrderBy(c => c.Id).Skip((page - 1) * size).Take(size)
            .Select(c => new Dictionary<string, object> { ["id"] = c.Id, ["name"] = c.Name, ["company_type"] = "Customer", ["archived"] = false })
            .ToList();
        return new Reply(200, new Dictionary<string, object> { ["companies"] = rows });
    }

    private Reply AddArticle(RecordedRequest request)
    {
        var body = request.Json();
        if (!body.TryGetProperty("article", out var article) || article.ValueKind != JsonValueKind.Object)
        {
            return new Reply(400, new { error = "param is missing or the value is empty: article" });
        }

        var name = article.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return FieldError("name", "can't be blank");
        }

        var companyId = article.TryGetProperty("company_id", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        if (!_companies.Exists(x => x.Id == companyId))
        {
            return FieldError("company", "must exist");
        }

        var content = article.TryGetProperty("content", out var html) && html.ValueKind == JsonValueKind.String ? html.GetString()! : string.Empty;
        var draft = article.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
        var id = _nextArticle++;
        var slug = NotSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        var saved = new HuduArticle(id, name, content, companyId, draft, new Uri(Site, $"kba/{slug}-{id}"));
        _articles.Add(saved);
        return new Reply(200, new Dictionary<string, object>
        {
            ["article"] = new Dictionary<string, object> { ["id"] = id, ["name"] = name, ["company_id"] = companyId, ["draft"] = draft, ["slug"] = $"{slug}-{id}", ["url"] = saved.Url.AbsoluteUri },
        });
    }

    private Reply AddUpload(RecordedRequest request)
    {
        if (!request.Form.TryGetValue("uploadable_type", out var type) || type != "Article")
        {
            return FieldError("uploadable_type", "is not included in the list");
        }

        if (!request.Form.TryGetValue("uploadable_id", out var raw) || !int.TryParse(raw, CultureInfo.InvariantCulture, out var articleId) || !_articles.Exists(a => a.Id == articleId))
        {
            return FieldError("uploadable", "must exist");
        }

        if (request.Files.SingleOrDefault(f => f.Field == "file") is not { Length: > 0 } file)
        {
            return FieldError("file", "can't be blank");
        }

        var saved = new HuduUpload(_nextUpload++, type, articleId, file.FileName, file.ContentType, file.Length);
        _uploads.Add(saved);
        return new Reply(200, new Dictionary<string, object>
        {
            ["upload"] = new Dictionary<string, object> { ["id"] = saved.Id, ["uploadable_type"] = type, ["uploadable_id"] = articleId, ["name"] = file.FileName },
        });
    }
}
