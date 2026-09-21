using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// ST-063. What a model may not be allowed to say.
///
/// The note prompt asks for a great deal of restraint — cite only real evidence, quote only what was
/// said, leave redactions alone, never write an instruction — and a prompt is a request. This is the
/// part that refuses. It runs on every draft before one is stored, so that a model having an off day
/// produces a rejected draft and a queued retry rather than a confident, wrong note in a customer's
/// ticket.
///
/// The same rules run in <c>research/prompts/checks.py</c> against the eval corpus. They are here as
/// well because that harness runs offline and this one runs in front of a technician.
/// </summary>
public sealed class DraftValidatorTests
{
    [Fact]
    public void AGoodDraftPasses()
    {
        var reasons = DraftValidator.Check(Draft(), Bundle());

        Assert.Empty(reasons);
    }

    [Fact]
    public void WhatTheModelInventedIsQuotedBackShortAndOnOneLine()
    {
        // 2026-09-20 review. Every reason interpolated a model-chosen string -- a frame id, a segment
        // id, a confidence word -- and those reasons travel back to the client and into the stored
        // draft-failed reason. The model reads OCR of a customer's screen, so a screen can suggest what
        // it writes: a "frame id" of a thousand characters with newlines in it forges log lines and
        // pushes screen content somewhere it was never meant to be (INV-10).
        //
        // It still has to be recognisable, or the reason stops telling a technician which citation was
        // wrong. Short, one line, and only the characters an id could really have.
        var forged = "f1\nERROR real-looking log line\r\n" + new string('x', 500);
        var draft = Draft() with
        {
            Steps = [Step("Restarted the spooler.", frames: [forged], transcript: ["t1"])],
        };

        var reason = Assert.Single(DraftValidator.Check(draft, Bundle()), r => r.Contains("frame", StringComparison.Ordinal));

        Assert.DoesNotContain('\n', reason);
        Assert.DoesNotContain('\r', reason);
        Assert.True(reason.Length < 200, $"A reason of {reason.Length} characters is not a sentence.");
        Assert.Contains("f1", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AStepCitingAFrameThatIsNotInTheSessionIsRefused()
    {
        // The model invented a frame id, which means the step it supports was invented too. Review shows
        // frame references as chips a technician clicks; a dangling one is a broken promise of evidence.
        var draft = Draft() with
        {
            Steps = [Step("Restarted the spooler.", frames: ["f-does-not-exist"], transcript: ["t1"])],
        };

        Assert.Contains(DraftValidator.Check(draft, Bundle()), r => r.Contains("f-does-not-exist", StringComparison.Ordinal));
    }

    [Fact]
    public void AStepCitingAFrameTheTechnicianRemovedIsRefused()
    {
        // They looked at that screenshot and said no. A note that cites it anyway publishes the thing
        // they removed, in words.
        var bundle = Bundle() with
        {
            Frames = [new BundleFrame("f1", 1_000, "Services", Excluded: true)],
        };

        Assert.Contains(DraftValidator.Check(Draft(), bundle), r => r.Contains("removed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AStepCitingATranscriptSegmentThatDoesNotExistIsRefused()
    {
        var draft = Draft() with
        {
            Steps = [Step("Restarted the spooler.", frames: ["f1"], transcript: ["t-nope"])],
        };

        Assert.Contains(DraftValidator.Check(draft, Bundle()), r => r.Contains("t-nope", StringComparison.Ordinal));
    }

    [Fact]
    public void AStepNobodySpokeAboutCannotBeHighConfidence()
    {
        // Prompt rule 3. Review marks low-confidence steps with a warning the technician has to clear,
        // so a step inferred from pixels alone that claims high confidence removes the one signal
        // telling them to look closer.
        var draft = Draft() with
        {
            Steps = [Step("Restarted the spooler.", frames: ["f1"], transcript: [], confidence: "high")],
        };

        Assert.Contains(DraftValidator.Check(draft, Bundle()), r => r.Contains("confidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AStepNobodySpokeAboutIsFineWhenItSaysSo()
    {
        var draft = Draft() with
        {
            Steps = [Step("Restarted the spooler.", frames: ["f1"], transcript: [], confidence: "low")],
        };

        Assert.Empty(DraftValidator.Check(draft, Bundle()));
    }

    [Fact]
    public void AQuotationNobodyUtteredIsRefused()
    {
        // Prompt rule 4. A quoted sentence in a ticket note is a claim about what somebody said, and a
        // customer may read it. Paraphrase is fine; invented speech is not.
        var draft = Draft() with
        {
            Steps = [Step("The technician said \"I have disabled the firewall\".", frames: ["f1"], transcript: ["t1"])],
        };

        Assert.Contains(DraftValidator.Check(draft, Bundle()), r => r.Contains("quot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AQuotationSomebodyDidUtterIsFine()
    {
        var draft = Draft() with
        {
            Steps = [Step("They said \"clearing the queue now\".", frames: ["f1"], transcript: ["t1"])],
        };

        Assert.Empty(DraftValidator.Check(draft, Bundle()));
    }

    [Theory]
    [InlineData("The password is Summer2024.")]
    [InlineData("Card 4111 1111 1111 1111 was on screen.")]
    [InlineData("SSN 123-45-6789 appeared.")]
    public void ACredentialThatReachedTheModelIsNotWrittenBackOut(string text)
    {
        // Redaction should have caught these upstream. If one arrives anyway, the last place it can be
        // stopped is before it is stored — after that it is in a ticket, an email and a customer's inbox.
        var draft = Draft() with { Steps = [Step(text, frames: ["f1"], transcript: ["t1"])] };

        Assert.NotEmpty(DraftValidator.Check(draft, Bundle()));
    }

    [Theory]
    [InlineData("Download the agent from https://example.com/install.")]
    [InlineData("Run Set-ExecutionPolicy Bypass to fix it.")]
    [InlineData("Disable Windows Defender and try again.")]
    public void ANoteThatReadsAsAnInstructionIsRefused(string text)
    {
        // Prompt rule 5a, and the prompt-injection defence. A session is a record of what was done. Text
        // on a customer's screen can ask a model to emit a command; the note is where that would land,
        // and somebody downstream would run it.
        var draft = Draft() with { FollowUps = [text] };

        Assert.NotEmpty(DraftValidator.Check(draft, Bundle()));
    }

    [Fact]
    public void TheRulesApplyToEveryFieldAndNotOnlyToSteps()
    {
        // These are the fields most likely to be copied into a customer-visible note. The Python checks
        // learned this the hard way: a follow-up saying "install the agent from …" once passed because
        // only step text was examined.
        var withProblem = Draft() with { Problem = "The password is Summer2024." };
        var withTitle = Draft() with { SuggestedTitle = "Run Set-ExecutionPolicy Bypass" };
        var withReason = Draft() with { KbReason = "They said \"never said this\"." };

        Assert.NotEmpty(DraftValidator.Check(withProblem, Bundle()));
        Assert.NotEmpty(DraftValidator.Check(withTitle, Bundle()));
        Assert.NotEmpty(DraftValidator.Check(withReason, Bundle()));
    }

    [Fact]
    public void ATimeLongerThanTheSessionIsRefused()
    {
        // The client rounds for billing, so the model's number is unrounded active time. One longer than
        // the session is a made-up number about something a customer is charged for.
        var draft = Draft() with { SuggestedTimeMinutes = 90 };

        Assert.Contains(DraftValidator.Check(draft, Bundle()), r => r.Contains("time", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnEmptyReasonForTheArticleDecisionIsRefused()
    {
        // Somebody has to read it to decide whether to publish a knowledge-base article.
        var draft = Draft() with { KbReason = "  " };

        Assert.NotEmpty(DraftValidator.Check(draft, Bundle()));
    }

    [Fact]
    public void ADraftFromADifferentPromptIsRefused()
    {
        // The post-conditions are written against one prompt's contract. A draft from another version
        // has not been checked by anything that understands it.
        var draft = Draft() with { PromptVersion = "note_v0" };

        Assert.Contains(DraftValidator.Check(draft, Bundle()), r => r.Contains("prompt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RedactionMarkersAreLeftAlone()
    {
        // Prompt rule 5. Writing about a credential is fine and is often the whole point of the note.
        var draft = Draft() with
        {
            Problem = "Outlook kept prompting for a password; the field showed [REDACTED].",
        };

        Assert.Empty(DraftValidator.Check(draft, Bundle()));
    }

    private static DraftJson Draft() => new()
    {
        Problem = "Nothing would print from the reception workstation.",
        Steps = [Step("Found the Print Spooler service stopped.", frames: ["f1"], transcript: ["t1"])],
        Result = "Printing works again.",
        FollowUps = ["Check the driver version if it stops again."],
        SuggestedTitle = "Printer offline — print spooler stopped",
        SuggestedTimeMinutes = 10,
        KbCandidate = true,
        KbReason = "A common fix worth writing down.",
        Source = "cloud",
        PromptVersion = "note_v1",
    };

    private static DraftStepJson Step(string text, string[] frames, string[] transcript, string confidence = "high") =>
        new()
        {
            Text = text,
            Confidence = confidence,
            FrameRefs = frames,
            TranscriptRefs = transcript,
        };

    private static SummarizeBundle Bundle() => new()
    {
        SessionId = "s1",
        DurationMs = 20 * 60 * 1000,
        Frames = [new BundleFrame("f1", 1_000, "Services Print Spooler Stopped", Excluded: false)],
        Transcript = [new BundleSegment("t1", 1_200, "clearing the queue now")],
    };
}
