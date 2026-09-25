using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTail.Api.Providers.ConnectWise;

/// <summary>
/// ConnectWise Manage's REST API, spoken as <see cref="IPsaProvider"/> (ST-091, ST-092).
///
/// Basic auth with the tenant's <c>companyId+publicKey:privateKey</c> exactly as the vault holds it, the
/// vendor <c>clientId</c> on every request, and the API's own words in every refusal. A 429 or an outage
/// is retried with exponential backoff and jitter, three attempts at most; a wrong key is not retried,
/// because that is how an integration becomes an account lockout.
///
/// The base address is the tenant's site plus <c>/v4_6_release/apis/3.0/</c>; the factory sets it. Every
/// route below is relative to that.
/// </summary>
public sealed class ConnectWiseProvider(HttpClient http, string credential, ConnectWiseOptions options, TimeProvider? time = null) : IPsaProvider
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly string _basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(credential ?? throw new ArgumentNullException(nameof(credential))));
    private readonly ConnectWiseOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => "connectwise";

    public async Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default)
    {
        var answer = await SendAsync(() => Get("system/info"), ct).ConfigureAwait(false);
        return answer.Ok ? ProviderResult.Success(true) : ProviderResult.Failure<bool>(answer.Error!);
    }

    /// <summary>
    /// A number is looked up as an id first and keyword matches follow it; a word searches open tickets'
    /// summaries newest first, a page at a time (ST-092). A ticket that is not there is an empty list,
    /// not an error: "no such ticket" is an answer.
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<TicketRef>>> SearchTicketsAsync(string query, CancellationToken ct = default)
    {
        var trimmed = (query ?? string.Empty).Trim();
        var numeric = trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit);
        if (trimmed.Length < 3 && !numeric)
        {
            return ProviderResult.Failure<IReadOnlyList<TicketRef>>(new ProviderError(
                ProviderErrorKind.Invalid,
                "The search needs at least three characters.",
                "Type a little more of the ticket number or summary."));
        }

        var found = new List<TicketRef>();
        if (numeric)
        {
            var exact = await SendAsync(() => Get($"service/tickets/{trimmed}"), ct).ConfigureAwait(false);
            if (exact.Ok)
            {
                found.Add(Ticket(exact.Value!));
            }
            else if (exact.Error!.Kind != ProviderErrorKind.NotFound)
            {
                return ProviderResult.Failure<IReadOnlyList<TicketRef>>(exact.Error);
            }
        }

        if (trimmed.Length >= 3)
        {
            // ConnectWise's conditions language quotes strings with single quotes and has no escaping a
            // caller can rely on; what the technician typed is reduced to characters that cannot end
            // the string early or start a second condition.
            var safe = new string(trimmed.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '#').ToArray()).Trim();
            if (safe.Length >= 3)
            {
                var conditions = $"summary contains '{safe}' and closedFlag = false";
                var route = $"service/tickets?conditions={Uri.EscapeDataString(conditions)}&orderBy={Uri.EscapeDataString("dateEntered desc")}&pageSize={_options.PageSize}&page=1&fields={Uri.EscapeDataString("id,summary,company/name,status/name")}";
                var page = await SendAsync(() => Get(route), ct).ConfigureAwait(false);
                if (!page.Ok)
                {
                    return ProviderResult.Failure<IReadOnlyList<TicketRef>>(page.Error!);
                }

                foreach (var element in page.Value!.EnumerateArray())
                {
                    var ticket = Ticket(element);
                    if (!found.Exists(f => f.Id == ticket.Id))
                    {
                        found.Add(ticket);
                    }
                }
            }
        }

        return ProviderResult.Success<IReadOnlyList<TicketRef>>([.. found.Take(_options.PageSize)]);
    }

    /// <summary>
    /// The note, then its attachments as documents on the ticket. An attachment that fails after the
    /// note landed is reported with the note's id in the message, because a retry that posts the note
    /// again is the duplicate ST-094 forbids; ST-093 owns attaching to a note that already exists.
    /// </summary>
    public async Task<ProviderResult<PublishedNote>> AddNoteAsync(TicketNote note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        var body = new
        {
            text = note.Body,
            internalAnalysisFlag = note.Internal,
            detailDescriptionFlag = !note.Internal,
            customerUpdatedFlag = false,
        };
        var posted = await SendAsync(() => Post($"service/tickets/{note.TicketId}/notes", body), ct).ConfigureAwait(false);
        if (!posted.Ok)
        {
            return ProviderResult.Failure<PublishedNote>(posted.Error!);
        }

        var id = posted.Value!.GetProperty("id").GetRawText();
        var attached = 0;
        foreach (var attachment in note.Attachments)
        {
            var document = await SendAsync(() => Document(note.TicketId, attachment), ct).ConfigureAwait(false);
            if (!document.Ok)
            {
                return ProviderResult.Failure<PublishedNote>(document.Error! with
                {
                    What = $"The note was published as {id}, but attachment {attached + 1} of {note.Attachments.Count} was refused: {document.Error.What}",
                });
            }

            attached++;
        }

        return ProviderResult.Success(new PublishedNote(id, null));
    }

    /// <summary>ST-094: minutes become a start and an end on the ticket, with the note as the description.</summary>
    public async Task<ProviderResult<PublishedNote>> AddTimeEntryAsync(TimeEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Minutes <= 0)
        {
            return ProviderResult.Failure<PublishedNote>(new ProviderError(
                ProviderErrorKind.Invalid,
                "A time entry of no minutes cannot be logged.",
                "Enter the minutes worked, or untick the time entry."));
        }

        if (!int.TryParse(entry.TicketId, NumberStyles.None, CultureInfo.InvariantCulture, out var ticketId))
        {
            return ProviderResult.Failure<PublishedNote>(new ProviderError(
                ProviderErrorKind.NotFound,
                $"ConnectWise ticket ids are numbers and '{entry.TicketId}' is not one.",
                "Pick the ticket again."));
        }

        var start = entry.StartedAt.ToUniversalTime();
        var body = new
        {
            chargeToId = ticketId,
            chargeToType = "ServiceTicket",
            timeStart = start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            timeEnd = start.AddMinutes(entry.Minutes).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            notes = entry.Notes,
            billableOption = entry.Billable ? "Billable" : "DoNotBill",
        };
        var posted = await SendAsync(() => Post("time/entries", body), ct).ConfigureAwait(false);
        return posted.Ok
            ? ProviderResult.Success(new PublishedNote(posted.Value!.GetProperty("id").GetRawText(), null))
            : ProviderResult.Failure<PublishedNote>(posted.Error!);
    }

    private HttpRequestMessage Get(string route) => Prepare(new HttpRequestMessage(HttpMethod.Get, route));

    private HttpRequestMessage Post(string route, object body) =>
        Prepare(new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: Wire) });

    private HttpRequestMessage Document(string ticketId, NoteAttachment attachment)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("Ticket"), "recordType" },
            { new StringContent(ticketId), "recordId" },
            { new StringContent(attachment.FileName), "title" },
        };
        var file = new ByteArrayContent(attachment.Bytes.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue(attachment.ContentType);
        form.Add(file, "file", attachment.FileName);
        return Prepare(new HttpRequestMessage(HttpMethod.Post, "system/documents") { Content = form });
    }

    private HttpRequestMessage Prepare(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basic);
        request.Headers.TryAddWithoutValidation("clientId", _options.ClientId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private Task<ProviderResult<JsonElement>> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        if (!_options.IsConfigured)
        {
            return Task.FromResult(ProviderResult.Failure<JsonElement>(new ProviderError(
                ProviderErrorKind.Invalid,
                "ConnectWise needs a clientId header and this deployment has none.",
                "Set ConnectWise:ClientId on the backend.")));
        }

        return ProviderHttp.SendAsync(
            _http,
            build,
            new RetryPolicy(_options.BackoffBase, _options.MaxAttempts),
            (status, body, retryAfter) => Failure(status, body, ProviderHttp.RetryAfter(retryAfter, _time)),
            "ConnectWise",
            _time,
            ct);
    }

    private static TicketRef Ticket(JsonElement element) => new(
        element.GetProperty("id").GetRawText(),
        element.TryGetProperty("summary", out var summary) ? summary.GetString() ?? string.Empty : string.Empty,
        element.TryGetProperty("company", out var company) && company.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
        element.TryGetProperty("status", out var status) && status.TryGetProperty("name", out var statusName) ? statusName.GetString() : null);

    /// <summary>
    /// The API's answer as a kind and two sentences. ConnectWise's own words about what it refused are
    /// kept — they are what a technician will paste into a ticket — and nothing else from the body is:
    /// no URLs, no stack, no token.
    /// </summary>
    private static ProviderError Failure(HttpStatusCode status, string body, TimeSpan? retryAfter) => status switch
    {
        HttpStatusCode.Unauthorized => new ProviderError(
            ProviderErrorKind.Unauthenticated,
            "ConnectWise rejected the API key.",
            "Update it in Settings → Integrations."),
        HttpStatusCode.Forbidden => new ProviderError(
            ProviderErrorKind.Forbidden,
            "ConnectWise says this API member may not do that.",
            "Ask your ConnectWise administrator for the permission."),
        HttpStatusCode.NotFound => new ProviderError(
            ProviderErrorKind.NotFound,
            "ConnectWise has no such ticket.",
            "Pick a different ticket."),
        HttpStatusCode.TooManyRequests => new ProviderError(
            ProviderErrorKind.Unavailable,
            "ConnectWise is rate-limiting requests.",
            "Try again in a minute.",
            retryAfter),
        >= HttpStatusCode.InternalServerError => new ProviderError(
            ProviderErrorKind.Unavailable,
            "ConnectWise did not answer.",
            "Try again in a minute.",
            retryAfter),
        _ => new ProviderError(
            ProviderErrorKind.Invalid,
            $"ConnectWise refused the request: {Reason(body)}.",
            "This is a ScreenTail bug; there is nothing to change on your side."),
    };

    private static string Reason(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var text = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                && errors[0].TryGetProperty("message", out var first)
                ? first.GetString()
                : root.TryGetProperty("message", out var message) ? message.GetString() : null;
            return ProviderHttp.Sanitise(text);
        }
        catch (JsonException)
        {
            return "no reason given";
        }
    }

}
