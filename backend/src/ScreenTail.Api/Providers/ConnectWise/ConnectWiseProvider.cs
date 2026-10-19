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
/// because that is how an integration becomes an account lockout. A note, time entry or document that
/// may already be saved is not sent again (<see cref="ProviderHttp"/>).
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

    /// <summary>How many screenshots go up at the same time.</summary>
    public const int AttachmentsAtOnce = AttachmentUploads.AtOnce;

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
            var exact = ProviderHttp.Read(await SendAsync(() => Get($"service/tickets/{trimmed}"), ct).ConfigureAwait(false), Ticket, "ConnectWise", written: false);
            if (exact.Ok)
            {
                found.Add(exact.Value!);
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
                var page = ProviderHttp.Read(await SendAsync(() => Get(route), ct).ConfigureAwait(false), Tickets, "ConnectWise", written: false);
                if (!page.Ok)
                {
                    return ProviderResult.Failure<IReadOnlyList<TicketRef>>(page.Error!);
                }

                foreach (var ticket in page.Value!)
                {
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
    /// ST-092's default: open tickets in the order ConnectWise last touched them. The API member is the
    /// tenant's, not the technician's, so "recently touched by me" is not a question this credential can
    /// ask; the tenant's recent activity is the nearest true answer.
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<TicketRef>>> RecentTicketsAsync(CancellationToken ct = default)
    {
        var route = $"service/tickets?conditions={Uri.EscapeDataString("closedFlag = false")}&orderBy={Uri.EscapeDataString("_info/lastUpdated desc")}&pageSize={_options.RecentCount}&page=1&fields={Uri.EscapeDataString("id,summary,company/name,status/name")}";
        var page = ProviderHttp.Read(await SendAsync(() => Get(route), ct).ConfigureAwait(false), Tickets, "ConnectWise", written: false);
        return page.Ok
            ? ProviderResult.Success<IReadOnlyList<TicketRef>>([.. page.Value!.Take(_options.RecentCount)])
            : ProviderResult.Failure<IReadOnlyList<TicketRef>>(page.Error!);
    }

    /// <summary>
    /// The note, then its attachments as documents on the ticket, private when the note is internal.
    /// Once the note has landed it is a success, with its id, whatever happens to the screenshots: one
    /// that did not attach is named in <see cref="PublishedNote.Incomplete"/>, because a failure is what
    /// Retry sends again and a second note is the duplicate ST-094 forbids. ST-093 owns attaching to a
    /// note that already exists.
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
        var posted = ProviderHttp.Read(await SendAsync(() => Post($"service/tickets/{note.TicketId}/notes", body), ct).ConfigureAwait(false), ProviderHttp.Id, "ConnectWise", written: true);
        if (!posted.Ok)
        {
            return ProviderResult.Failure<PublishedNote>(posted.Error!);
        }

        var id = posted.Value!;
        var missing = await AttachmentUploads.UploadAsync(
            note.Attachments,
            (attachment, token) => SendAsync(() => Document(note.TicketId, attachment, note.Internal), token),
            $"The note was published as {id}",
            "The note stays as it is; add the missing screenshots to the ticket by hand rather than publishing the note again.",
            ct).ConfigureAwait(false);

        return ProviderResult.Success(new PublishedNote(id, null, missing));
    }

    /// <summary>
    /// ST-094: minutes become a start and an end on the ticket, with the note as the description. The
    /// description of a time entry can reach the customer, on an invoice or in the portal, so an internal
    /// note's text goes in the internal notes instead (2026-10-12 review, S-83). Nothing is copied onto
    /// the ticket as another note, because the note destination already put it there, and nobody is
    /// emailed, because the note itself does not email either.
    /// </summary>
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
            notes = entry.Internal ? null : entry.Notes,
            internalNotes = entry.Internal ? entry.Notes : null,
            billableOption = entry.Billable ? "Billable" : "DoNotBill",
            addToDetailDescriptionFlag = false,
            addToInternalAnalysisFlag = false,
            addToResolutionFlag = false,
            emailResourceFlag = false,
            emailContactFlag = false,
            emailCcFlag = false,
        };
        var posted = ProviderHttp.Read(await SendAsync(() => Post("time/entries", body), ct).ConfigureAwait(false), ProviderHttp.Id, "ConnectWise", written: true);
        return posted.Ok
            ? ProviderResult.Success(new PublishedNote(posted.Value!, null))
            : ProviderResult.Failure<PublishedNote>(posted.Error!);
    }

    private HttpRequestMessage Get(string route) => Prepare(new HttpRequestMessage(HttpMethod.Get, route));

    private HttpRequestMessage Post(string route, object body) =>
        Prepare(new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body, options: Wire) });

    /// <summary>
    /// One screenshot as a document on the ticket. <c>privateFlag</c> follows the note type, so an internal
    /// note's screenshots are not left to the board's default for portal visibility (S-163). The bytes
    /// are sent from the buffer they were decoded into, not copied for each upload.
    /// </summary>
    private HttpRequestMessage Document(string ticketId, NoteAttachment attachment, bool isPrivate)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("Ticket"), "recordType" },
            { new StringContent(ticketId), "recordId" },
            { new StringContent(attachment.FileName), "title" },
            { new StringContent(isPrivate ? "true" : "false"), "privateFlag" },
        };
        var file = new ReadOnlyMemoryContent(attachment.Bytes);
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
            new RetryPolicy(_options.BackoffBase, _options.MaxAttempts) { MaxWait = _options.MaxRetryWait },
            (status, body, retryAfter) => Failure(status, body, ProviderHttp.RetryAfter(retryAfter, _time)),
            "ConnectWise",
            _time,
            ct);
    }

    private static List<TicketRef> Tickets(JsonElement page) => [.. page.EnumerateArray().Select(Ticket)];

    private static TicketRef Ticket(JsonElement element) => new(
        ProviderHttp.Id(element),
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
