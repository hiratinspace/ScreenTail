using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScreenTail.Api.Tests.Providers.StandIns;

/// <summary>A ticket on the stand-in, with the fields the provider reads or filters on.</summary>
internal sealed record CwTicket(int Id, string Summary, int CompanyId, string Company, string Status, bool Closed, DateTimeOffset Entered, DateTimeOffset LastUpdated);

/// <summary>A note the stand-in took, as ConnectWise would keep it.</summary>
internal sealed record CwNote(int Id, int TicketId, string Text, bool DetailDescription, bool InternalAnalysis, bool Resolution, bool CustomerUpdated, string Member);

/// <summary>A time entry the stand-in took. <paramref name="Member"/> is the member it was charged to.</summary>
internal sealed record CwTimeEntry(int Id, int TicketId, DateTimeOffset Start, DateTimeOffset End, string? Notes, string? InternalNotes, string BillableOption, string Member, IReadOnlyDictionary<string, bool> Flags);

/// <summary>A document the stand-in took against a ticket.</summary>
internal sealed record CwDocument(int Id, int TicketId, string Title, bool Private, string FileName, string ContentType, long Length);

/// <summary>
/// A stand-in ConnectWise Manage for the provider tests (ST-091 to ST-094): the routes
/// <c>ConnectWiseProvider</c> calls, under <c>/v4_6_release/apis/3.0/</c>, answered over HTTPS from
/// state it keeps, in the shapes the recorded fixtures (<c>ScriptedConnectWise</c>) already capture.
///
/// What it holds the provider to: Basic auth that decodes to exactly <c>companyId+publicKey:privateKey</c>
/// and a <c>clientId</c> header on every request (401 otherwise, never a hint which part was wrong);
/// ticket search by the <c>conditions</c>, <c>orderBy</c>, <c>fields</c>, <c>page</c> and
/// <c>pageSize</c> the provider sends, so a closed ticket, a wrong order or a field it forgot to ask for
/// shows up as a wrong result; a note that must carry text and one of the three note-type flags; a time
/// entry whose start, end, billing option and ticket are checked and which is charged to the
/// authenticated API member when it names none; a document that needs its ticket and a file. Refusals
/// use the error envelope the fixtures use (<c>code</c>, <c>message</c>, and for a field error an
/// <c>errors</c> list of <c>code</c>, <c>message</c>, <c>resource</c>, <c>field</c>).
///
/// What it is not: ConnectWise. The routes and field names are the provider's, checked against the
/// published API reference by whoever wrote the provider; the rules above are the stand-in's reading of
/// it. Only the sandbox check in <c>docs/integrations/connectwise.md</c> shows the vendor agrees.
/// </summary>
internal sealed partial class ConnectWiseStandIn : StandInHost
{
    public const string ApiRoot = "/v4_6_release/apis/3.0/";

    /// <summary>The flags on a time entry that copy it onto the ticket or email somebody about it.</summary>
    private static readonly string[] CopyAndEmailFlags = ["addToDetailDescriptionFlag", "addToInternalAnalysisFlag", "addToResolutionFlag", "emailResourceFlag", "emailContactFlag", "emailCcFlag"];

    private readonly List<CwTicket> _tickets = [];
    private readonly List<CwNote> _notes = [];
    private readonly List<CwTimeEntry> _timeEntries = [];
    private readonly List<CwDocument> _documents = [];
    private int _nextNote = 90001;
    private int _nextTimeEntry = 70001;
    private int _nextDocument = 55001;

    public ConnectWiseStandIn()
    {
        var seen = new DateTimeOffset(2026, 9, 24, 14, 0, 0, TimeSpan.Zero);
        _tickets.Add(new CwTicket(731946, "Printer offline in reception", 7, "Acme Dental", "In Progress", false, seen.AddHours(-3), seen.AddMinutes(-5)));
        _tickets.Add(new CwTicket(731950, "Printer driver on the new laptop", 9, "Bright Smiles", "New", false, seen.AddHours(-1), seen.AddHours(-1)));
        _tickets.Add(new CwTicket(731960, "VPN drops every hour", 8, "Borough Legal", "New", false, seen.AddHours(-2), seen.AddMinutes(-30)));
        _tickets.Add(new CwTicket(731900, "Printer toner replaced", 7, "Acme Dental", "Closed", true, seen.AddDays(-2), seen.AddDays(-1)));
    }

    /// <summary>The login company, lower case, as a tenant types it on ConnectWise's sign-in screen.</summary>
    public string CompanyId { get; } = "acme";

    public string PublicKey { get; } = RandomToken(8);

    public string PrivateKey { get; } = RandomToken(8);

    /// <summary>The vendor id the deployment holds (ConnectWise:ClientId). Made per run, like the keys.</summary>
    public string ClientId { get; } = Guid.NewGuid().ToString();

    /// <summary>The API member the keys belong to: who a time entry is charged to when it names nobody.</summary>
    public string ApiMember { get; } = "screentail-api";

    /// <summary>What the tenant stores: the three parts joined the way ConnectWise's Basic auth wants them.</summary>
    public string Credential => $"{CompanyId}+{PublicKey}:{PrivateKey}";

    /// <summary>Routes, by the path after the API root, this API member's role may not use: a 403.</summary>
    public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Refuse a time entry that names no member, with a field error. A real board may want one: a time
    /// entry charged to an API member is not something every ConnectWise accepts, and the provider sends
    /// none (ST-094 AC2 waits for ST-010's identities).
    /// </summary>
    public bool RequireMemberOnTimeEntries { get; set; }

    public IReadOnlyList<CwNote> Notes => Locked(() => _notes.ToList());

    public IReadOnlyList<CwTimeEntry> TimeEntries => Locked(() => _timeEntries.ToList());

    public IReadOnlyList<CwDocument> Documents => Locked(() => _documents.ToList());

    public IReadOnlyList<CwTicket> Tickets => Locked(() => _tickets.ToList());

    /// <summary>More open tickets whose summaries match, newest last, for a search that has more than one page.</summary>
    public void AddTickets(int count, string summary)
    {
        _ = Locked(() =>
        {
            var start = new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < count; i++)
            {
                _tickets.Add(new CwTicket(800000 + i, $"{summary} {i}", 9, "Bright Smiles", "New", false, start.AddMinutes(i), start.AddMinutes(i)));
            }

            return count;
        });
    }

    public static Reply FieldError(string resource, string field, string message) => new(400, new
    {
        code = "InvalidObject",
        message = $"{resource} object is invalid",
        errors = new[] { new { code = "InvalidField", message, resource, field } },
    });

    protected override Reply? Authenticate(RecordedRequest request)
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(Credential));
        var authorised = string.Equals(request.Header("Authorization"), expected, StringComparison.Ordinal)
            && string.Equals(request.Header("clientId"), ClientId, StringComparison.Ordinal);
        return authorised ? null : new Reply(401, new { code = "Unauthorized", message = "Authentication failed." });
    }

    protected override Reply Answer(RecordedRequest request)
    {
        if (!request.Path.StartsWith(ApiRoot, StringComparison.Ordinal))
        {
            return NotFound("The resource cannot be found.");
        }

        var route = request.Path[ApiRoot.Length..];
        var family = TicketRoute().IsMatch(route) ? "service/tickets" : route;
        if (Denied.Contains(route) || Denied.Contains(family))
        {
            return new Reply(403, new { code = "Forbidden", message = "You do not have permission to perform this action." });
        }

        return (request.Method, route) switch
        {
            ("GET", "system/info") => new Reply(200, new { version = "v2026.1", isCloud = true, serverTimeZone = "Eastern Standard Time" }),
            ("GET", "service/tickets") => ListTickets(request),
            ("GET", _) when TicketRoute().Match(route) is { Success: true, Groups: var g } && g["notes"].Length == 0 => GetTicket(int.Parse(g["id"].Value, CultureInfo.InvariantCulture), request),
            ("POST", _) when TicketRoute().Match(route) is { Success: true, Groups: var g } && g["notes"].Length > 0 => AddNote(int.Parse(g["id"].Value, CultureInfo.InvariantCulture), request),
            ("POST", "time/entries") => AddTimeEntry(request),
            ("POST", "system/documents") => AddDocument(request),
            _ => NotFound("The resource cannot be found."),
        };
    }

    [GeneratedRegex(@"^service/tickets/(?<id>[0-9]+)(?<notes>/notes)?$")]
    private static partial Regex TicketRoute();

    [GeneratedRegex(@"^summary contains '(?<value>[^']*)'$", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryContains();

    [GeneratedRegex(@"^closedFlag = (?<value>true|false)$", RegexOptions.IgnoreCase)]
    private static partial Regex ClosedFlag();

    private static Reply NotFound(string message) => new(404, new { code = "NotFound", message });

    private Reply ListTickets(RecordedRequest request)
    {
        IEnumerable<CwTicket> matched = _tickets;
        if (request.Query.TryGetValue("conditions", out var conditions))
        {
            foreach (var clause in conditions.Split(" and ", StringSplitOptions.TrimEntries))
            {
                if (SummaryContains().Match(clause) is { Success: true } contains)
                {
                    var value = contains.Groups["value"].Value;
                    matched = matched.Where(t => t.Summary.Contains(value, StringComparison.OrdinalIgnoreCase));
                }
                else if (ClosedFlag().Match(clause) is { Success: true } closed)
                {
                    var value = bool.Parse(closed.Groups["value"].Value);
                    matched = matched.Where(t => t.Closed == value);
                }
                else
                {
                    return FieldError("ticket", "conditions", $"The condition {clause} could not be parsed.");
                }
            }
        }

        if (request.Query.TryGetValue("orderBy", out var orderBy))
        {
            Func<CwTicket, object>? key = orderBy.Split(' ')[0] switch
            {
                "dateEntered" => t => t.Entered,
                "_info/lastUpdated" => t => t.LastUpdated,
                "id" => t => t.Id,
                _ => null,
            };
            if (key is null)
            {
                return FieldError("ticket", "orderBy", $"Cannot order by {orderBy}.");
            }

            matched = orderBy.EndsWith(" desc", StringComparison.OrdinalIgnoreCase) ? matched.OrderByDescending(key) : matched.OrderBy(key);
        }

        var size = request.Query.TryGetValue("pageSize", out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : 25;
        var page = request.Query.TryGetValue("page", out var p) ? int.Parse(p, CultureInfo.InvariantCulture) : 1;
        if (size is < 1 or > 1000 || page < 1)
        {
            return FieldError("ticket", "pageSize", "pageSize must be between 1 and 1000, and page at least 1.");
        }

        var all = matched.ToList();
        var rows = all.Skip((page - 1) * size).Take(size).Select(t => Project(TicketJson(t), request)).ToList();
        var headers = all.Count > page * size
            ? new Dictionary<string, string> { ["Link"] = $"<{request.Path}?page={page + 1}&pageSize={size}>; rel=\"next\"" }
            : null;
        return new Reply(200, rows, headers);
    }

    private Reply GetTicket(int id, RecordedRequest request)
    {
        var ticket = _tickets.Find(t => t.Id == id);
        return ticket is null ? NotFound("Ticket not found") : new Reply(200, Project(TicketJson(ticket), request));
    }

    private Reply AddNote(int ticketId, RecordedRequest request)
    {
        if (_tickets.Find(t => t.Id == ticketId) is null)
        {
            return NotFound("Ticket not found");
        }

        var note = request.Json();
        var text = Text(note, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return FieldError("ticketNote", "text", "The text field is required.");
        }

        bool detail = Flag(note, "detailDescriptionFlag"), internalAnalysis = Flag(note, "internalAnalysisFlag"), resolution = Flag(note, "resolutionFlag");
        if (!detail && !internalAnalysis && !resolution)
        {
            return FieldError("ticketNote", "detailDescriptionFlag", "A note must be at least one of detail description, internal analysis or resolution.");
        }

        var saved = new CwNote(_nextNote++, ticketId, text, detail, internalAnalysis, resolution, Flag(note, "customerUpdatedFlag"), ApiMember);
        _notes.Add(saved);
        return new Reply(201, new
        {
            id = saved.Id,
            ticketId,
            text,
            detailDescriptionFlag = detail,
            internalAnalysisFlag = internalAnalysis,
            resolutionFlag = resolution,
            member = new { identifier = ApiMember },
            dateCreated = request.At.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        });
    }

    private Reply AddTimeEntry(RecordedRequest request)
    {
        var entry = request.Json();
        if (Text(entry, "chargeToType") != "ServiceTicket")
        {
            return FieldError("timeEntry", "chargeToType", "chargeToType must be ServiceTicket for a ticket.");
        }

        var ticketId = entry.TryGetProperty("chargeToId", out var charge) && charge.ValueKind == JsonValueKind.Number ? charge.GetInt32() : 0;
        if (_tickets.Find(t => t.Id == ticketId) is null)
        {
            return FieldError("timeEntry", "chargeToId", $"The ticket {ticketId} was not found.");
        }

        if (!DateTimeOffset.TryParse(Text(entry, "timeStart"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)
            || !DateTimeOffset.TryParse(Text(entry, "timeEnd"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end))
        {
            return FieldError("timeEntry", "timeStart", "timeStart and timeEnd are required.");
        }

        if (end <= start)
        {
            return FieldError("timeEntry", "timeEnd", "timeEnd must be after timeStart.");
        }

        var billable = Text(entry, "billableOption") ?? "Billable";
        if (billable is not ("Billable" or "DoNotBill" or "NoCharge" or "NoDefault"))
        {
            return FieldError("timeEntry", "billableOption", $"{billable} is not a billable option.");
        }

        var member = entry.TryGetProperty("member", out var named) && named.TryGetProperty("identifier", out var identifier) ? identifier.GetString() : null;
        if (member is null && RequireMemberOnTimeEntries)
        {
            return FieldError("timeEntry", "member", "The member field is required for a time entry on this board.");
        }

        var flags = CopyAndEmailFlags.ToDictionary(f => f, f => Flag(entry, f), StringComparer.Ordinal);
        var saved = new CwTimeEntry(_nextTimeEntry++, ticketId, start.ToUniversalTime(), end.ToUniversalTime(), Text(entry, "notes"), Text(entry, "internalNotes"), billable, member ?? ApiMember, flags);
        _timeEntries.Add(saved);
        return new Reply(201, new
        {
            id = saved.Id,
            chargeToId = ticketId,
            chargeToType = "ServiceTicket",
            member = new { identifier = saved.Member },
            timeStart = saved.Start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            timeEnd = saved.End.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            actualHours = (decimal)(saved.End - saved.Start).TotalHours,
            billableOption = billable,
        });
    }

    private Reply AddDocument(RecordedRequest request)
    {
        if (!request.Form.TryGetValue("recordType", out var type) || type != "Ticket")
        {
            return FieldError("document", "recordType", "recordType is required.");
        }

        if (!request.Form.TryGetValue("recordId", out var record) || !int.TryParse(record, CultureInfo.InvariantCulture, out var ticketId) || _tickets.Find(t => t.Id == ticketId) is null)
        {
            return FieldError("document", "recordId", "The record was not found.");
        }

        if (request.Files.SingleOrDefault(f => f.Field == "file") is not { Length: > 0 } file)
        {
            return FieldError("document", "file", "A file is required.");
        }

        var saved = new CwDocument(
            _nextDocument++,
            ticketId,
            request.Form.TryGetValue("title", out var title) ? title : file.FileName,
            request.Form.TryGetValue("privateFlag", out var flag) && bool.Parse(flag),
            file.FileName,
            file.ContentType,
            file.Length);
        _documents.Add(saved);
        return new Reply(201, new { id = saved.Id, title = saved.Title, fileName = saved.FileName, privateFlag = saved.Private });
    }

    private static JsonObject TicketJson(CwTicket t) => new()
    {
        ["id"] = t.Id,
        ["summary"] = t.Summary,
        ["company"] = new JsonObject { ["id"] = t.CompanyId, ["identifier"] = t.Company.Replace(' ', '_').ToUpperInvariant(), ["name"] = t.Company },
        ["status"] = new JsonObject { ["id"] = t.Closed ? 9 : 3, ["name"] = t.Status },
        ["closedFlag"] = t.Closed,
        ["dateEntered"] = t.Entered.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        ["_info"] = new JsonObject { ["lastUpdated"] = t.LastUpdated.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
    };

    /// <summary>
    /// Only the fields asked for, when <c>fields</c> is given: <c>company/name</c> is the company object
    /// with its name and nothing else. A provider that reads a field it did not ask for finds it missing.
    /// </summary>
    private static JsonObject Project(JsonObject full, RecordedRequest request)
    {
        if (!request.Query.TryGetValue("fields", out var fields))
        {
            return full;
        }

        var projected = new JsonObject();
        foreach (var path in fields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = path.Split('/');
            JsonNode? source = full;
            var target = projected;
            for (var i = 0; i < parts.Length && source is JsonObject from; i++)
            {
                source = from[parts[i]];
                if (source is null)
                {
                    break;
                }

                if (i == parts.Length - 1)
                {
                    target[parts[i]] = source.DeepClone();
                }
                else
                {
                    target = target[parts[i]] as JsonObject ?? (JsonObject)(target[parts[i]] = new JsonObject());
                }
            }
        }

        return projected;
    }

    private static string? Text(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
