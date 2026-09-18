using ScreenTail.Core.Notifications;
using ScreenTail.Core.Outbox;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Notifications;

/// <summary>
/// ST-073. What ScreenTail says when something happens, and what it says when something goes wrong.
///
/// The messages are Spec §6's, verbatim, and asserted verbatim. That looks like over-testing until you
/// remember what they are: the only words a technician reads while their attention is on a customer's
/// machine. "Something went wrong" costs a support call; a sentence that names the object and offers the
/// next step costs nothing.
///
/// The rule underneath all of them is the failure rule. A draft that could not be written must never take
/// the session with it — the screenshots and the transcript are still there, they are what the technician
/// came for, and an error page where the work used to be is the worst possible answer.
/// </summary>
public sealed class NotificationTests
{
    [Fact]
    public void ADraftThatIsReadyNamesTheTicketAndOffersReview()
    {
        var notice = Notices.For(Capture(CaptureStates.DraftReady, "Printer offline", "Acme Dental"));

        Assert.NotNull(notice);
        Assert.Equal("Draft ready — Printer offline · Acme Dental", notice.Text);
        Assert.Equal(NotificationAction.Review, notice.Action);
    }

    [Fact]
    public void ADraftThatCouldNotBeWrittenSaysTheSessionIsStillThere()
    {
        // Spec §6, and the whole point of the sentence: the second half is what stops a technician
        // thinking the session is gone and starting the job again from memory.
        var notice = Notices.For(Capture(CaptureStates.DraftFailed, reason: "The provider did not answer."));

        Assert.NotNull(notice);
        Assert.Equal("We couldn't draft this session. Your screenshots and transcript are saved.", notice.Text);
        Assert.Equal(NotificationAction.Open, notice.Action);
    }

    [Fact]
    public void WaitingForANetworkSaysSoAndOffersNothing()
    {
        // The one message with no action, deliberately. There is nothing to press: it will happen when
        // the network comes back, and a button that cannot help is a button that wastes a press.
        var notice = Notices.For(
            Capture(CaptureStates.Idle),
            new OutboxWaiting(Drafts: 1, Publishes: 0, Uncertain: 0));

        Assert.NotNull(notice);
        Assert.Equal("Offline — your draft will be created when you're back online.", notice.Text);
        Assert.Equal(NotificationAction.None, notice.Action);
    }

    [Fact]
    public void RecordingSaysNothingAtAll()
    {
        // Spec §4: never interrupt recording. A toast over a customer's screen during a support call is
        // the one notification that can do real damage.
        Assert.Null(Notices.For(Capture(CaptureStates.Recording)));
        Assert.Null(Notices.For(Capture(CaptureStates.Suppressed)));
        Assert.Null(Notices.For(Capture(CaptureStates.Paused)));
    }

    [Fact]
    public void ADraftWaitingIsNotAnnouncedWhileASessionIsStillRunning()
    {
        // A queued draft from the last session must not interrupt this one.
        Assert.Null(Notices.For(
            Capture(CaptureStates.Recording),
            new OutboxWaiting(Drafts: 1, Publishes: 0, Uncertain: 0)));
    }

    [Fact]
    public void NothingIsSaidTwiceForTheSameThing()
    {
        // A state the service repeats — a reconnect, a get_state — must not produce a second toast. Two
        // identical notifications say the thing happened twice.
        var notifier = new Notifier();
        var state = Capture(CaptureStates.DraftReady, "Printer offline", "Acme Dental");

        Assert.NotNull(notifier.Observe(state));
        Assert.Null(notifier.Observe(state));
        Assert.Null(notifier.Observe(state));
    }

    [Fact]
    public void ASecondDraftIsAnnouncedAgain()
    {
        var notifier = new Notifier();

        Assert.NotNull(notifier.Observe(Capture(CaptureStates.DraftReady, "First", "Acme Dental")));
        Assert.NotNull(notifier.Observe(Capture(CaptureStates.DraftReady, "Second", "Acme Dental")));
    }

    [Fact]
    public void EveryMessageFollowsTheCopyRules()
    {
        // Spec §6: sentence case, no exclamation marks, name the object. Checked here rather than in
        // review, because copy drifts one well-meant edit at a time.
        var notices = new[]
        {
            Notices.For(Capture(CaptureStates.DraftReady, "Printer offline", "Acme Dental")),
            Notices.For(Capture(CaptureStates.DraftFailed, reason: "x")),
            Notices.For(Capture(CaptureStates.Idle), new OutboxWaiting(1, 0, 0)),
            Notices.For(Capture(CaptureStates.Idle), new OutboxWaiting(0, 0, 1)),
        };

        foreach (var notice in notices)
        {
            Assert.NotNull(notice);
            Assert.False(string.IsNullOrWhiteSpace(notice.Text));

            // Sentence case and no exclamation marks, which is what Spec §6 actually asks for. Not a
            // trailing full stop: "Draft ready — Printer offline · Acme Dental" is a label rather than a
            // sentence, and the spec writes it without one.
            Assert.DoesNotContain("!", notice.Text, StringComparison.Ordinal);
            Assert.True(char.IsUpper(notice.Text[0]), $"'{notice.Text}' should start with a capital");
            Assert.NotEqual(notice.Text.ToUpperInvariant(), notice.Text);
        }
    }

    [Fact]
    public void WorkNobodyKnowsTheOutcomeOfAsksForAPersonRatherThanRetrying()
    {
        // The outbox parks an attempt whose result is unknown, because publishing twice is worse than
        // publishing late. Nothing will move it on its own, so it is the one queue state that has to say
        // something.
        var notice = Notices.For(
            Capture(CaptureStates.Idle),
            new OutboxWaiting(Drafts: 0, Publishes: 0, Uncertain: 1));

        Assert.NotNull(notice);
        Assert.Contains("check", notice.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NotificationAction.Open, notice.Action);
    }

    [Theory]
    [InlineData("The provider did not answer.", true)]
    [InlineData("No summarization provider is configured on this deployment, so this session was not drafted.", false)]
    public void RetryIsOnlyOfferedWhenRetryingCouldWork(string reason, bool expected)
    {
        // Spec v0.4.3 settled this shape of question once already, for the offline case: a button that
        // tells you to retry something that cannot succeed is worse than no button, because it makes the
        // technician think the problem is theirs. The same reasoning applies to a deployment with no
        // provider configured — every retry fails in the same millisecond.
        Assert.Equal(expected, DraftFailure.CanRetry(reason));
    }

    [Fact]
    public void AFailedDraftAlwaysLeavesTheSessionReachable()
    {
        // ST-073's second criterion, and the rule the whole ticket exists for. The screenshots and the
        // transcript are what the technician came back for; a draft that could not be written must not
        // take them with it.
        var failure = DraftFailure.For("The provider did not answer.");

        Assert.True(failure.ShowTimeline);
        Assert.True(failure.ShowFrames);
        Assert.True(failure.ShowTranscript);
        Assert.Equal("We couldn't draft this session. Your screenshots and transcript are saved.", DraftFailure.Headline);
    }

    [Fact]
    public void AFailureThatCannotBeRetriedStillLeavesTheSessionReachable()
    {
        var failure = DraftFailure.For("No summarization provider is configured on this deployment, so this session was not drafted.");

        Assert.False(failure.CanRetryNow);
        Assert.True(failure.ShowTimeline);
        Assert.True(failure.ShowFrames);
        Assert.True(failure.ShowTranscript);
    }

    [Fact]
    public void PublishIsDisabledWithAReasonThatSaysWhatHappensNext()
    {
        // Spec §5 S3 as amended by v0.4.3: the reason sits beside the button rather than in a tooltip,
        // because a tooltip on a disabled control is the one place a keyboard user can never reach it.
        Assert.Equal(
            "There is no note to publish yet. Retry the draft first.",
            DraftFailure.For("The provider did not answer.").PublishDisabledReason);

        Assert.Equal(
            "There is no note to publish yet. It will be drafted when you are back online.",
            DraftFailure.Offline.PublishDisabledReason);
    }

    private static CaptureStateSnapshot Capture(string state, string? title = null, string? company = null, string? reason = null) => new()
    {
        State = state,
        SessionId = title is null ? "s1" : $"s-{title}",
        DraftTitle = title,
        DraftCompany = company,
        DraftFailureReason = reason,
    };
}
