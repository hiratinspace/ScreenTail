using System.Globalization;
using System.Net;
using System.Text;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Publish;

/// <summary>
/// The article as it lands in the knowledge base (ST-096): the note's sections as headings, the steps
/// as a list, the ticket named at the foot, and every piece of text escaped — text on a customer's
/// screen steers the note (T7), and in a knowledge base it would also render.
/// </summary>
public static class KbArticleFormatter
{
    public static string Html(DraftJson note, string ticketId, int frameCount)
    {
        ArgumentNullException.ThrowIfNull(note);
        var html = new StringBuilder();
        Section(html, "Problem", note.Problem);

        if (note.Steps.Count > 0)
        {
            html.Append("<h3>Steps</h3><ol>");
            foreach (var step in note.Steps)
            {
                html.Append("<li>").Append(WebUtility.HtmlEncode(step.Text)).Append("</li>");
            }

            html.Append("</ol>");
        }

        Section(html, "Result", note.Result);

        if (note.FollowUps.Count > 0)
        {
            html.Append("<h3>Follow-ups</h3><ul>");
            foreach (var item in note.FollowUps)
            {
                html.Append("<li>").Append(WebUtility.HtmlEncode(item)).Append("</li>");
            }

            html.Append("</ul>");
        }

        var screenshots = frameCount switch
        {
            0 => string.Empty,
            1 => " 1 screenshot is attached to this article.",
            _ => string.Create(CultureInfo.InvariantCulture, $" {frameCount} screenshots are attached to this article."),
        };
        html.Append("<p>From ticket #").Append(WebUtility.HtmlEncode(ticketId)).Append('.').Append(screenshots).Append("</p>");
        html.Append("<p><em>Drafted with ScreenTail and reviewed by a technician.</em></p>");
        return html.ToString();
    }

    private static void Section(StringBuilder html, string heading, string? body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            html.Append("<h3>").Append(heading).Append("</h3><p>").Append(WebUtility.HtmlEncode(body.Trim())).Append("</p>");
        }
    }
}
