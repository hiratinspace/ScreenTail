using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using ScreenTail.Api.Providers.Demo;

namespace ScreenTail.Api.Endpoints;

/// <summary>
/// What the demo PSA holds, for showing an audience that the note landed on the ticket (ST-090).
///
/// Mapped only when <c>Providers:Demo</c> is on, which the service allows only in Development, and
/// answered only to a browser on this machine: a caller from anywhere else gets the same 404 as a route
/// that does not exist. There is no token to present, the way there is none for the API document in
/// Development, so a browser on the laptop can open the page while the audience watches. Nothing here is
/// under /v1, and nothing here writes.
///
/// "This machine" is four checks, because each alone can be talked around (review 2026-10-27). The peer
/// and the listening address are both loopback; the request carries no proxy header, because with
/// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> the framework believes any <c>X-Forwarded-For</c> and a
/// LAN caller claiming 127.0.0.1 becomes it, and a proxy or tunnel on the laptop connects from loopback for
/// somebody else; and the Host is this machine's own name, so a page that rebinds its DNS name to
/// 127.0.0.1 cannot read the JSON from the audience's browser.
/// </summary>
public static class DemoPsaEndpoint
{
    public const string Root = "/dev/demo-psa";

    /// <summary>A strict page: no script, no frames, no requests anywhere. The note's text is shown as text.</summary>
    private const string Policy = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static IEndpointRouteBuilder MapDemoPsa(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup(Root)
            .AllowAnonymous()
            .ExcludeFromDescription()
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                if (!OnlyThisMachine(http))
                {
                    return Results.NotFound();
                }

                // On every answer, the JSON as much as the pages: it holds the note's text.
                http.Response.Headers.XContentTypeOptions = "nosniff";
                http.Response.Headers.CacheControl = "no-store";
                return await next(context).ConfigureAwait(false);
            });

        _ = group.MapGet("/", (HttpContext http, DemoStore store) => Page(http, Index(store)))
            .WithName("DemoPsaIndex");

        _ = group.MapGet("/tickets/{id}", (string id, DemoStore store) => store.Find(id) is { } ticket
                ? Results.Ok(new DemoTicketView(DemoNames.PsaTitle, ticket.Ticket.Id, ticket.Ticket.Summary, ticket.Ticket.Company, ticket.Ticket.Status, ticket.Notes, ticket.TimeEntries))
                : Results.NotFound())
            .WithName("DemoPsaTicket");

        _ = group.MapGet("/tickets/{id}/view", (string id, HttpContext http, DemoStore store) => store.Find(id) is { } ticket
                ? Page(http, Ticket(ticket))
                : Results.NotFound())
            .WithName("DemoPsaTicketPage");

        return app;
    }

    /// <summary>What a proxy or a tunnel adds, and a browser opened on this machine never sends.</summary>
    private static readonly string[] ProxyHeaders = ["Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Original-For", "X-Original-Host", "X-Real-IP"];

    /// <summary>The names this machine answers to, without the port.</summary>
    private static readonly string[] LocalNames = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>The loopback address, either family. Null is a caller nobody can vouch for, and is refused.</summary>
    public static bool FromThisMachine(IPAddress? remote) =>
        remote is not null && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);

    /// <summary>A browser on this machine, asking for this machine by name, with no proxy between.</summary>
    public static bool OnlyThisMachine(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return FromThisMachine(http.Connection.RemoteIpAddress)
            && FromThisMachine(http.Connection.LocalIpAddress)
            && !ProxyHeaders.Any(http.Request.Headers.ContainsKey)
            && LocalNames.Contains(http.Request.Host.Host, StringComparer.OrdinalIgnoreCase);
    }

    private static IResult Page(HttpContext http, string html)
    {
        http.Response.Headers.ContentSecurityPolicy = Policy;
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
    }

    private static string Index(DemoStore store)
    {
        var html = Start("Demo PSA tickets");
        _ = html.Append("<h1>Tickets</h1><table><thead><tr><th>Ticket</th><th>Summary</th><th>Company</th><th>Status</th><th>Notes</th><th>Time</th></tr></thead><tbody>");
        foreach (var ticket in store.Tickets())
        {
            var held = store.Find(ticket.Id)!;
            _ = html.Append("<tr><td><a href=\"").Append(E(TicketPath(ticket.Id))).Append("\">#").Append(E(ticket.Id)).Append("</a></td><td>")
                .Append(E(ticket.Summary)).Append("</td><td>").Append(E(ticket.Company)).Append("</td><td>").Append(E(ticket.Status ?? string.Empty))
                .Append("</td><td>").Append(held.Notes.Count.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(held.TimeEntries.Sum(t => t.Minutes).ToString(CultureInfo.InvariantCulture)).Append(" min</td></tr>");
        }

        _ = html.Append("</tbody></table>");

        var articles = store.Articles();
        _ = html.Append("<h2>").Append(E(DemoNames.KbTitle)).Append("</h2>");
        if (articles.Count == 0)
        {
            _ = html.Append("<p class=\"muted\">No articles yet.</p>");
        }

        foreach (var article in articles.Reverse())
        {
            _ = html.Append("<div class=\"card\"><div class=\"meta\">").Append(article.Draft ? "Draft article" : "Article").Append(" · ").Append(E(article.Company))
                .Append(" · ").Append(E(When(article.At))).Append(Screenshots(article.Screenshots)).Append("</div><div class=\"title\">")
                .Append(E(article.Title)).Append("</div></div>");
        }

        return End(html);
    }

    private static string Ticket(DemoTicket ticket)
    {
        var html = Start($"#{ticket.Ticket.Id} {ticket.Ticket.Summary}");
        _ = html.Append("<p><a href=\"").Append(Root).Append("\">All demo tickets</a></p>")
            .Append("<h1>#").Append(E(ticket.Ticket.Id)).Append(' ').Append(E(ticket.Ticket.Summary)).Append("</h1>")
            .Append("<p class=\"muted\">").Append(E(ticket.Ticket.Company)).Append(" · ").Append(E(ticket.Ticket.Status ?? string.Empty)).Append("</p>");

        _ = html.Append("<h2>Notes</h2>");
        if (ticket.Notes.Count == 0)
        {
            _ = html.Append("<p class=\"muted\">Nothing published to this ticket yet. This page refreshes every five seconds.</p>");
        }

        foreach (var note in ticket.Notes.Reverse())
        {
            _ = html.Append("<div class=\"card\"><div class=\"meta\"><span class=\"tag\">").Append(note.Internal ? "Internal" : "Discussion").Append("</span> ")
                .Append(E(When(note.At))).Append(Screenshots(note.Screenshots)).Append("</div><pre>").Append(E(note.Body)).Append("</pre></div>");
        }

        _ = html.Append("<h2>Time entries</h2>");
        if (ticket.TimeEntries.Count == 0)
        {
            _ = html.Append("<p class=\"muted\">None yet.</p>");
        }
        else
        {
            _ = html.Append("<table><thead><tr><th>Started</th><th>Minutes</th><th>Billable</th><th>Note type</th></tr></thead><tbody>");
            foreach (var entry in ticket.TimeEntries.Reverse())
            {
                _ = html.Append("<tr><td>").Append(E(When(entry.StartedAt))).Append("</td><td>").Append(entry.Minutes.ToString(CultureInfo.InvariantCulture))
                    .Append("</td><td>").Append(entry.Billable ? "Yes" : "No").Append("</td><td>").Append(entry.Internal ? "Internal" : "Discussion").Append("</td></tr>");
            }

            _ = html.Append("</tbody></table>");
        }

        return End(html);
    }

    private static StringBuilder Start(string title) => new StringBuilder()
        .Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"5\">")
        .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>").Append(E(title)).Append(" · ").Append(E(DemoNames.PsaTitle)).Append("</title>")
        .Append("<style>")
        .Append("body{font:15px/1.5 system-ui,-apple-system,'Segoe UI',sans-serif;margin:0;color:#1d2433;background:#f6f7f9}")
        .Append("main{max-width:860px;margin:0 auto;padding:24px}")
        .Append(".banner{background:#fff4d6;border-bottom:1px solid #e8cf86;padding:12px 24px;font-weight:600}")
        .Append(".banner span{font-weight:400}")
        .Append("h1{font-size:22px;margin:8px 0 4px}h2{font-size:17px;margin:28px 0 8px}")
        .Append(".muted,.meta{color:#5b6475}.meta{font-size:13px;margin-bottom:6px}")
        .Append(".card{background:#fff;border:1px solid #dde1e8;border-radius:8px;padding:14px 16px;margin:0 0 12px}")
        .Append(".title{font-weight:600}.tag{background:#e7ecf5;border-radius:4px;padding:1px 6px;color:#1d2433}")
        .Append("pre{white-space:pre-wrap;font:inherit;margin:0}")
        .Append("table{border-collapse:collapse;width:100%;background:#fff;border:1px solid #dde1e8}")
        .Append("th,td{text-align:left;padding:8px 10px;border-bottom:1px solid #eef0f4}th{font-size:13px;color:#5b6475}")
        .Append("a{color:#2350b5}")
        .Append("</style></head><body><div class=\"banner\">").Append(E(DemoNames.PsaTitle))
        .Append(" <span>· kept in this backend's memory, Development only. Nothing here is a real ticket, and it is gone when the backend stops.</span></div><main>");

    private static string End(StringBuilder html) => html.Append("</main></body></html>").ToString();

    private static string TicketPath(string id) => $"{Root}/tickets/{Uri.EscapeDataString(id)}/view";

    private static string When(DateTimeOffset at) => at.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Screenshots(int count) => count switch
    {
        0 => string.Empty,
        1 => " · 1 screenshot sent (not kept by the demo)",
        _ => string.Create(CultureInfo.InvariantCulture, $" · {count} screenshots sent (not kept by the demo)"),
    };

    private static string E(string text) => HtmlEncoder.Default.Encode(text);
}

/// <summary>A demo ticket and everything published to it, as <c>GET /dev/demo-psa/tickets/{id}</c> answers.</summary>
public sealed record DemoTicketView(
    string Provider,
    string Id,
    string Summary,
    string Company,
    string? Status,
    IReadOnlyList<DemoNote> Notes,
    IReadOnlyList<DemoTimeEntry> TimeEntries);
