using ScreenTail.Api.Publish;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Publish;

/// <summary>
/// The note as it lands on a ticket (ST-093 AC1, AC3): the editor's four sections in the editor's
/// order, steps numbered, screenshots referred to by their place in the strip, redaction left alone,
/// and the footer that says a person reviewed it.
/// </summary>
public sealed class NoteFormatterTests
{
    [Fact]
    public void TheFourSectionsInTheEditorsOrderWithNumberedSteps()
    {
        var text = NoteFormatter.Render(Note(), frameIds: ["f-0002"], reviewer: "A Technician", footer: true);

        Assert.Equal(
            """
            Problem
            Printer offline in reception.

            Steps
            1. Checked the spooler service; it was stopped. (screenshot 1)
            2. Restarted the spooler and set it to automatic.

            Result
            Printing works again.

            Follow-ups
            - Check the driver on the second printer.

            Drafted with ScreenTail, reviewed by A Technician.
            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void ARedactionMarkerIsLeftExactlyAsItIs()
    {
        var note = Note() with { Problem = "The password was [REDACTED] and the card ended [REDACTED]." };

        var text = NoteFormatter.Render(note, [], null, footer: false);

        Assert.Contains("[REDACTED] and the card ended [REDACTED].", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFooterIsConfigurableAndNamesNobodyWhenThereIsNobody()
    {
        Assert.DoesNotContain("Drafted with", NoteFormatter.Render(Note(), [], "A Technician", footer: false), StringComparison.Ordinal);
        Assert.EndsWith("Drafted with ScreenTail.", NoteFormatter.Render(Note(), [], reviewer: null, footer: true), StringComparison.Ordinal);
    }

    [Fact]
    public void AScreenshotReferenceIsItsPlaceAmongTheOnesSentNotItsId()
    {
        // Ids mean nothing on a ticket; "screenshot 2" means the second attachment. A step that cites a
        // frame the technician excluded cites nothing.
        var note = Note() with
        {
            Steps =
            [
                new DraftStepJson { Text = "Two frames.", Confidence = "high", FrameRefs = ["f-0009", "f-0002"] },
                new DraftStepJson { Text = "An excluded one.", Confidence = "high", FrameRefs = ["f-0001"] },
            ],
        };

        var text = NoteFormatter.Render(note, frameIds: ["f-0002", "f-0009"], null, footer: false);

        Assert.Contains("1. Two frames. (screenshots 1, 2)", text, StringComparison.Ordinal);
        Assert.Contains("2. An excluded one.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySectionsAreLeftOut()
    {
        var note = Note() with { FollowUps = [], Result = string.Empty };

        var text = NoteFormatter.Render(note, [], null, footer: false);

        Assert.DoesNotContain("Follow-ups", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Result", text, StringComparison.Ordinal);
    }

    private static DraftJson Note() => new()
    {
        Problem = "Printer offline in reception.",
        Steps =
        [
            new DraftStepJson { Text = "Checked the spooler service; it was stopped.", Confidence = "high", FrameRefs = ["f-0002"] },
            new DraftStepJson { Text = "Restarted the spooler and set it to automatic.", Confidence = "high" },
        ],
        Result = "Printing works again.",
        FollowUps = ["Check the driver on the second printer."],
        SuggestedTitle = "Printer offline — Acme Dental",
        SuggestedTimeMinutes = 23,
        KbCandidate = false,
        KbReason = "Not a KB candidate: one-off fix",
        Source = "cloud",
        PromptVersion = "note_v1",
    };
}
