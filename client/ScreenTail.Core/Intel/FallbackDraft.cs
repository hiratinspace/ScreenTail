using System.Globalization;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Intel;

/// <summary>
/// The draft written on this device when no model can be reached (ST-065 AC3): local-only mode, or no
/// backend configured. One step per thing the technician said, each pointing at the screenshot the
/// aligner tied it to; when nothing was said, one step per screenshot. Every reference names something
/// the session holds, so the eval harness's hallucination count is zero by construction; every step is
/// low confidence and the note is marked local, which Review shows as a banner.
///
/// A worse note than the model's, and the reason Review exists. Better than "we couldn't draft this
/// session" for a session the technician, or their policy, chose to keep on the device.
/// </summary>
public static class FallbackDraft
{
    public const string PromptVersion = "fallback-v1";

    /// <summary>Enough steps to review; past this the note says how many more screenshots there were.</summary>
    public const int MaxSteps = 40;

    public static DraftNote Build(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var showable = session.Frames
            .Where(f => !f.RedactionPending && !f.ExcludedByUser)
            .OrderBy(f => f.TsMs)
            .ToList();
        var showableIds = showable.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var marked = session.Events.OfType<MarkerEvent>().Select(m => m.FrameId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal)!;
        var said = session.Transcript.Where(t => !string.IsNullOrWhiteSpace(t.Text)).OrderBy(t => t.TsMs).ToList();

        var steps = new List<DraftStep>();
        var left = 0;
        if (said.Count > 0)
        {
            foreach (var segment in said.Take(MaxSteps))
            {
                steps.Add(new DraftStep
                {
                    Text = Sentence(segment.Text),
                    Confidence = StepConfidence.Low,
                    FrameRefs = segment.FrameId is { } frame ? [frame] : [],
                    TranscriptRefs = [segment.Id],
                });
            }

            left = said.Count - steps.Count;
        }
        else
        {
            foreach (var frame in showable.Take(MaxSteps))
            {
                steps.Add(new DraftStep
                {
                    Text = marked.Contains(frame.Id)
                        ? $"Screenshot at {Clock(frame.TsMs)}, marked by you."
                        : $"Screenshot at {Clock(frame.TsMs)}.",
                    Confidence = StepConfidence.Low,
                    FrameRefs = [frame.Id],
                });
            }

            left = showable.Count - steps.Count;
        }

        var tool = ToolName(session.RemoteTool.Kind);
        var minutes = session.DurationMs is { } ms ? (long)Math.Round(ms / 60_000d) : 0;
        var problem = said.Count > 0
            ? Sentence(said[0].Text)
            : showable.Count > 0
                ? $"A {tool} session with {showable.Count} screenshot{(showable.Count == 1 ? string.Empty : "s")} and no narration."
                : $"A {tool} session in which nothing was captured.";
        var result = "Not recorded — add the outcome before publishing."
            + (left > 0 ? $" {left} more {(said.Count > 0 ? "things were said" : "screenshots were taken")} than fit here; see the timeline." : string.Empty);

        return new DraftNote
        {
            Problem = problem,
            Steps = steps,
            Result = result,
            FollowUps = [],
            SuggestedTitle = $"{tool} session, {session.StartedAt.ToLocalTime():d MMM HH:mm}",
            SuggestedTimeMinutes = minutes,
            KbCandidate = false,
            KbReason = "Drafted on this device without a model.",
            Source = DraftSource.Local,
            PromptVersion = PromptVersion,
        };
    }

    /// <summary>What was said, as a sentence: a capital to start, a full stop to end, nothing else changed.</summary>
    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var capitalised = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return capitalised[^1] is '.' or '!' or '?' ? capitalised : capitalised + ".";
    }

    private static string Clock(long ms) => TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string ToolName(RemoteToolKind kind) => kind switch
    {
        RemoteToolKind.Screenconnect => "ScreenConnect",
        RemoteToolKind.Rdp => "Remote Desktop",
        RemoteToolKind.Browser => "browser",
        _ => kind.ToString(),
    };
}
