using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Review;

/// <summary>
/// ST-076. The bottom panel of Review: what happened, when, and what was said about it (Spec §5 S3).
///
/// This is the panel that decides whether a thirty-second review is possible. The note claims things; the
/// timeline is where a technician checks them, by clicking a sentence and seeing the screenshot it came
/// from. Without that the only way to verify a draft is to read the whole session, which is the work the
/// product exists to remove.
/// </summary>
public sealed class TimelineTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 18, 14, 2, 0, TimeSpan.Zero);

    [Fact]
    public void ClickingALineFindsTheFrameItWasAbout()
    {
        // ST-076 AC1, and the point of the panel. The aligner decided which screenshot each sentence
        // belongs to (ST-028); this is where a technician gets to use that.
        var timeline = new SessionTimeline(Session(
            frames: [Frame("f1", 10_000)],
            transcript: [Said("t1", 9_500, 12_000, "clearing the queue now", frameId: "f1")]));

        var line = Assert.Single(timeline.Lines);

        Assert.Equal("f1", line.FrameId);
    }

    [Fact]
    public void ASentenceWithNoScreenshotJumpsNowhereRatherThanGuessing()
    {
        // Narration: the technician explained something they did not screenshot. Sending the filmstrip to
        // the nearest frame would show them a picture of something else and call it evidence.
        var timeline = new SessionTimeline(Session(
            frames: [Frame("f1", 90_000)],
            transcript: [Said("t1", 1_000, 3_000, "just looking at the event log")]));

        Assert.Null(Assert.Single(timeline.Lines).FrameId);
    }

    [Fact]
    public void LinesAreStampedWithTheWallClockTimeTheySpeakOf()
    {
        // Spec §5 S3: "[14:02:10] clearing the queue now". Wall clock rather than session-relative,
        // because a technician cross-checking against a customer's event log is reading clock times.
        var timeline = new SessionTimeline(Session(
            frames: [],
            transcript: [Said("t1", 10_000, 12_000, "clearing the queue now")]));

        Assert.Equal("14:02:10", Assert.Single(timeline.Lines).At);
    }

    [Fact]
    public void WhatWasMaskedStaysMaskedInTheTranscript()
    {
        // ST-076 AC2. The transcript scrubber replaced it on the way in (ST-042); this is the panel that
        // would otherwise be the one place it reappeared, in a monospace font, next to a screenshot of
        // the screen it was typed into.
        var timeline = new SessionTimeline(Session(
            frames: [],
            transcript: [Said("t1", 1_000, 3_000, "the password is [REDACTED] and then I signed in")]));

        var line = Assert.Single(timeline.Lines);

        Assert.Contains("[REDACTED]", line.Text, StringComparison.Ordinal);
        Assert.True(line.HasRedaction);
    }

    [Fact]
    public void EveryKindOfMarkerTheSpecListsIsProduced()
    {
        // Spec §5 S3: clicks are dots, frames are squares, suppression is striped, scope is a band, and
        // a marked moment is a flag. Each is a different question a technician asks of the timeline.
        var timeline = new SessionTimeline(Session(
            frames: [Frame("f1", 5_000)],
            transcript: [Said("t1", 20_000, 21_000, "there it is")],
            events:
            [
                new ClickEvent { TsMs = 1_000, X = 4, Y = 5, Button = MouseButton.Left },
                new MarkerEvent { TsMs = 8_000, FrameId = "f1" },
                new CaptureStateEvent { TsMs = 10_000, State = CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField },
                new CaptureStateEvent { TsMs = 15_000, State = CaptureState.Recording },
                new CaptureStateEvent { TsMs = 30_000, State = CaptureState.Suppressed, Reason = CaptureStateReason.OutOfScope },
                new CaptureStateEvent { TsMs = 35_000, State = CaptureState.Recording },
            ]));

        Assert.Contains(timeline.Markers, m => m.Kind == TimelineMarkerKind.Click);
        Assert.Contains(timeline.Markers, m => m.Kind == TimelineMarkerKind.Frame);
        Assert.Contains(timeline.Markers, m => m.Kind == TimelineMarkerKind.Marked);
        Assert.Contains(timeline.Markers, m => m.Kind == TimelineMarkerKind.Suppressed);
        Assert.Contains(timeline.Markers, m => m.Kind == TimelineMarkerKind.OutOfScope);
        Assert.Contains(timeline.Markers, m => m.Kind == TimelineMarkerKind.Narration);
    }

    [Fact]
    public void SuppressionIsABandAndNotAPoint()
    {
        // A gap has a length, and its length is the thing worth seeing: ten seconds of a password field
        // and four minutes of one are different facts about a session.
        var timeline = new SessionTimeline(Session(
            frames: [],
            events:
            [
                new CaptureStateEvent { TsMs = 10_000, State = CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField },
                new CaptureStateEvent { TsMs = 25_000, State = CaptureState.Recording },
            ]));

        var band = Assert.Single(timeline.Markers, m => m.Kind == TimelineMarkerKind.Suppressed);

        Assert.Equal(10_000, band.TsMs);
        Assert.Equal(25_000, band.EndMs);
        Assert.Equal("Capture paused — a password field had focus", band.Description);
    }

    [Fact]
    public void ASuppressionThatNeverEndsRunsToTheEndOfTheSession()
    {
        // The session stopped while suppressed. Drawing it as a point would say capture came back, which
        // is the one thing nobody knows.
        var timeline = new SessionTimeline(Session(
            frames: [],
            durationMs: 60_000,
            events: [new CaptureStateEvent { TsMs = 40_000, State = CaptureState.Suppressed }]));

        var band = Assert.Single(timeline.Markers, m => m.Kind == TimelineMarkerKind.Suppressed);

        Assert.Equal(60_000, band.EndMs);
    }

    [Fact]
    public void MarkersComeBackInTimeOrder()
    {
        var timeline = new SessionTimeline(Session(
            frames: [Frame("f1", 9_000)],
            events:
            [
                new ClickEvent { TsMs = 12_000, X = 1, Y = 1, Button = MouseButton.Left },
                new ClickEvent { TsMs = 3_000, X = 1, Y = 1, Button = MouseButton.Left },
            ]));

        var times = timeline.Markers.Select(m => m.TsMs).ToList();

        Assert.Equal(times.OrderBy(t => t), times);
    }

    [Fact]
    public void ASessionWithNothingInItStillDraws()
    {
        // A session where capture never started. The panel is empty rather than absent, because an empty
        // scrubber says "nothing happened" and a missing one says "something is broken".
        var timeline = new SessionTimeline(Session(frames: [], durationMs: 5_000));

        Assert.Empty(timeline.Markers);
        Assert.Empty(timeline.Lines);
        Assert.Equal(5_000, timeline.DurationMs);
    }

    [Fact]
    public void ASessionWithNoDurationStillHasAScaleToDrawOn()
    {
        // Division by zero in a scrubber is a blank panel. The last thing that happened is the end.
        var timeline = new SessionTimeline(Session(
            frames: [Frame("f1", 30_000)],
            durationMs: null));

        Assert.True(timeline.DurationMs >= 30_000);
    }

    [Fact]
    public void AMarkersPositionIsAFractionOfTheSession()
    {
        // So the view can draw it without knowing how long anything is.
        var timeline = new SessionTimeline(Session(
            frames: [Frame("f1", 30_000)],
            durationMs: 60_000));

        Assert.Equal(0.5, Assert.Single(timeline.Markers).Position, 3);
    }

    [Fact]
    public void ClicksAreThinnedSoTheScrubberStaysReadable()
    {
        // A busy minute is two hundred clicks. Two hundred dots in a 40-pixel strip is a grey line, which
        // tells a technician less than ten dots would.
        var clicks = Enumerable.Range(0, 400)
            .Select(i => (SessionEvent)new ClickEvent { TsMs = i * 100, X = 1, Y = 1, Button = MouseButton.Left })
            .ToList();

        var timeline = new SessionTimeline(Session(frames: [], durationMs: 40_000, events: clicks));

        var dots = timeline.Markers.Count(m => m.Kind == TimelineMarkerKind.Click);
        Assert.True(dots > 0 && dots <= SessionTimeline.MaxClickMarkers, $"{dots} dots is too many to read");
    }

    [Fact]
    public void FramesAreNeverThinnedBecauseEachOneIsEvidence()
    {
        // Unlike clicks. A frame marker is something a technician can click through to; losing one loses
        // the only route to that screenshot from the timeline.
        var frames = Enumerable.Range(0, 60).Select(i => Frame($"f{i:D2}", i * 1_000)).ToList();

        var timeline = new SessionTimeline(Session(frames: frames, durationMs: 60_000));

        Assert.Equal(60, timeline.Markers.Count(m => m.Kind == TimelineMarkerKind.Frame));
    }

    [Fact]
    public void APendingFrameIsNotOnTheTimeline()
    {
        // INV-1 reaches here too: a marker that jumps to a frame nobody has read is a route to an
        // unredacted screenshot.
        var timeline = new SessionTimeline(Session(
            frames:
            [
                Frame("ok", 1_000),
                Frame("pending", 2_000) with { RedactionPending = true, RedactedAt = null },
            ]));

        var frame = Assert.Single(timeline.Markers, m => m.Kind == TimelineMarkerKind.Frame);
        Assert.Equal("ok", frame.FrameId);
    }

    [Fact]
    public void ThePanelRemembersWhetherItWasOpen()
    {
        // Spec §5 S3: collapsed by default, Alt+T toggles, and the choice persists. A technician who
        // works with it open should not reopen it for every session.
        var state = new TimelinePanelState(expanded: false);

        Assert.False(state.Expanded);
        Assert.Equal(SessionTimeline.CollapsedHeight, state.Height);

        state.Toggle();

        Assert.True(state.Expanded);
        Assert.Equal(SessionTimeline.ExpandedHeight, state.Height);
    }

    private static Session Session(
        IReadOnlyList<Frame> frames,
        IReadOnlyList<TranscriptSegment>? transcript = null,
        IReadOnlyList<SessionEvent>? events = null,
        long? durationMs = 60_000) => new()
        {
            SchemaVersion = "session.v1",
            SessionId = "s1",
            StartedAt = Started,
            DurationMs = durationMs,
            RemoteTool = new RemoteTool { Kind = RemoteToolKind.Screenconnect },
            PartialCapture = false,
            FramesPurgedUnredacted = 0,
            LocalOnly = false,
            Events = events ?? [],
            Frames = frames,
            Transcript = transcript ?? [],
        };

    private static Frame Frame(string id, long tsMs) => new()
    {
        Id = id,
        TsMs = tsMs,
        Trigger = FrameTrigger.Click,
        Image = "aW1hZ2U=",
        Width = 1920,
        Height = 1080,
        RedactionPending = false,
        RedactedAt = Started,
        MaskedRegions = [],
        SensitiveContext = false,
        ExcludedByUser = false,
        OcrText = "Services",
    };

    private static TranscriptSegment Said(string id, long tsMs, long endMs, string text, string? frameId = null) => new()
    {
        Id = id,
        TsMs = tsMs,
        EndMs = endMs,
        Speaker = Speaker.Tech,
        Text = text,
        Confidence = 0.9,
        FrameId = frameId,
    };
}
