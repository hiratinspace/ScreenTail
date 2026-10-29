using System.Globalization;
using System.Text;
using System.Text.Json;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Providers.Llm;

/// <summary>
/// A drafting provider that is not a model (ST-013): <c>Summarization:Provider=scripted</c>.
///
/// It exists so the whole loop can run where there is no model key and must be no bill: the Windows
/// end-to-end job in CI, and an offline rehearsal on the laptop. It writes a note from the bundle it was
/// given, the same way every time: one step per screenshot, quoting nothing and copying the redaction
/// worker's own text for that screenshot (already masked on the device), plus how many transcript
/// segments came with it. It costs nothing and calls nobody.
///
/// <b>It says what it is, in the note.</b> Every note it writes reads "drafted by the scripted test
/// provider" in its problem and its result, so a note that somehow reached a real ticket could not be
/// taken for a model's.
///
/// <b>It is refused outside Development</b> (<see cref="SummarizationOptions.Validate"/>), exactly as
/// <c>Providers:Demo</c> is: a deployment with it on would hand technicians a note nobody drafted.
///
/// The note goes through the same validator as a model's. When the validator refuses it (a screen whose
/// text reads as an instruction, say) the repair asks again, and the second answer leaves the screens'
/// text out and keeps only the counts, which no rule can object to.
/// </summary>
public sealed class ScriptedProvider : ILlmProvider
{
    /// <summary>The words every note it writes carries, so nobody can take one for a model's.</summary>
    public const string Label = "drafted by the scripted test provider";

    /// <summary>The provider name in the cost ledger and the logs.</summary>
    public const string ProviderName = "scripted-test-provider";

    /// <summary>How much of one screenshot's text a step carries. A note is read in thirty seconds.</summary>
    private const int MaxExcerpt = 240;

    /// <summary>How many steps at most: one per screenshot, up to this many.</summary>
    private const int MaxSteps = 12;

    public string Name => ProviderName;

    public Task<ProviderResult<LlmDraft>> DraftAsync(SummarizeBundle bundle, string? repair, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var draft = Write(bundle, withScreenText: repair is null);
        return Task.FromResult(ProviderResult.Success(new LlmDraft(JsonSerializer.Serialize(draft), 0m)));
    }

    /// <summary>The note for this bundle. Deterministic: the same bundle always gets the same note.</summary>
    internal static DraftJson Write(SummarizeBundle bundle, bool withScreenText)
    {
        var frames = bundle.Frames.Where(frame => !frame.Excluded).ToList();
        var segments = bundle.Transcript.Count;
        var counts = string.Create(
            CultureInfo.InvariantCulture,
            $"{frames.Count} screenshot(s) and {segments} transcript segment(s)");

        var steps = new List<DraftStepJson>();
        for (var i = 0; i < Math.Min(frames.Count, MaxSteps); i++)
        {
            var frame = frames[i];
            var text = withScreenText ? Excerpt(frame.OcrText) : null;
            steps.Add(new DraftStepJson
            {
                Text = text is { Length: > 0 }
                    ? string.Create(CultureInfo.InvariantCulture, $"Screenshot {i + 1} showed: {text}")
                    : string.Create(CultureInfo.InvariantCulture, $"Screenshot {i + 1} was captured."),

                // No transcript evidence, so low: the validator holds every note to that, a scripted one too.
                Confidence = "low",
                FrameRefs = [frame.Id],
            });
        }

        if (segments > 0)
        {
            steps.Add(new DraftStepJson
            {
                Text = string.Create(CultureInfo.InvariantCulture, $"{segments} transcript segment(s) were heard."),
                Confidence = "low",
                TranscriptRefs = [bundle.Transcript[0].Id],
            });
        }

        // Whole minutes of the session, never more: the validator refuses a time longer than the session.
        var minutes = bundle.DurationMs is { } duration && duration > 0 ? duration / 60_000 : 0;

        return new DraftJson
        {
            Problem = $"A test session with {counts}, {Label}, not by a model.",
            Steps = steps,
            Result = $"This note was {Label} (Development only). Nothing in it was written by a model.",
            FollowUps = [],
            SuggestedTitle = string.Create(CultureInfo.InvariantCulture, $"Scripted test note: {frames.Count} screenshot(s)"),
            SuggestedTimeMinutes = minutes,
            KbCandidate = false,
            KbReason = "A scripted test note is never a knowledge-base article.",
            Source = "cloud",
            PromptVersion = DraftValidator.PromptVersion,
        };
    }

    /// <summary>
    /// The screenshot's text as the device masked it, on one line, with every quotation mark taken out
    /// (a quotation in a note is a claim somebody said it) and cut to a length a note can carry.
    /// </summary>
    private static string? Excerpt(string? ocr)
    {
        if (string.IsNullOrWhiteSpace(ocr))
        {
            return null;
        }

        var line = new StringBuilder(Math.Min(ocr.Length, MaxExcerpt + 8));
        var space = false;
        foreach (var c in ocr)
        {
            if (c is '"' or '\'' or '`' or '‘' or '’' or '“' or '”')
            {
                continue;
            }

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = line.Length > 0;
                continue;
            }

            if (space)
            {
                _ = line.Append(' ');
                space = false;
            }

            _ = line.Append(c);
            if (line.Length >= MaxExcerpt)
            {
                break;
            }
        }

        return line.Length == 0 ? null : line.ToString();
    }
}
