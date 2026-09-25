using System.Globalization;
using System.Text;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Publish;

/// <summary>
/// The note as it lands on a ticket (ST-093 AC1): the editor's four sections in the editor's order,
/// steps numbered, screenshots referred to by their place among the ones attached, and a footer that
/// says a person reviewed it. Plain text with newlines, because every PSA accepts that and formatting
/// that one renders is what another shows as tags.
///
/// Nothing is escaped or rewritten: a <c>[REDACTED]</c> marker is a promise the note keeps by being
/// left exactly as it is.
/// </summary>
public static class NoteFormatter
{
    public static string Render(DraftJson note, IReadOnlyList<string> frameIds, string? reviewer, bool footer)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(frameIds);
        var text = new StringBuilder();

        Section(text, "Problem", note.Problem);

        if (note.Steps.Count > 0)
        {
            var steps = new StringBuilder();
            for (var i = 0; i < note.Steps.Count; i++)
            {
                var step = note.Steps[i];
                steps.Append(CultureInfo.InvariantCulture, $"{i + 1}. {step.Text}");
                var places = step.FrameRefs
                    .Select(id => frameIds.ToList().IndexOf(id))
                    .Where(index => index >= 0)
                    .Select(index => index + 1)
                    .Order()
                    .ToList();
                if (places.Count == 1)
                {
                    steps.Append(CultureInfo.InvariantCulture, $" (screenshot {places[0]})");
                }
                else if (places.Count > 1)
                {
                    steps.Append(CultureInfo.InvariantCulture, $" (screenshots {string.Join(", ", places)})");
                }

                steps.Append('\n');
            }

            Section(text, "Steps", steps.ToString().TrimEnd('\n'));
        }

        Section(text, "Result", note.Result);

        if (note.FollowUps.Count > 0)
        {
            Section(text, "Follow-ups", string.Join('\n', note.FollowUps.Select(item => "- " + item)));
        }

        if (footer)
        {
            text.Append(string.IsNullOrWhiteSpace(reviewer)
                ? "Drafted with ScreenTail."
                : $"Drafted with ScreenTail, reviewed by {reviewer.Trim()}.");
            text.Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    private static void Section(StringBuilder text, string heading, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        text.Append(heading).Append('\n').Append(body.Trim()).Append("\n\n");
    }
}
