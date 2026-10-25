using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>ST-075: the centre pane's contents and what publishing actually attaches (Spec §5 S3).</summary>
public sealed class FilmstripTests
{
    [Fact]
    public void TheHeaderCountsWhatWillBeAttached()
    {
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000), Frame("f2", 2000, excluded: true), Frame("f3", 3000)));

        Assert.Equal("2 of 3 included", strip.Header);
    }

    [Fact]
    public void AnExcludedFrameIsAbsentFromWhatPublishesRatherThanMarked()
    {
        // The AC is "excluded frame never attached on publish". A publisher that had to remember to check
        // a flag is a publisher that one day forgets, so the excluded frame is not in the list at all.
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000), Frame("f2", 2000)));

        strip.Toggle("f2");

        Assert.Equal(["f1"], strip.ToPublish().Select(frame => frame.Id));
    }

    [Fact]
    public void TheStripOpensWithWhateverTheTechnicianDecidedLastTime()
    {
        // excluded_by_user is persisted, so reopening Review must not quietly re-include a frame somebody
        // took out — the most likely reason it is out is that it should not leave the building.
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000, excluded: true)));

        Assert.False(strip.IsIncluded("f1"));
        Assert.Empty(strip.ToPublish());
    }

    [Fact]
    public void ASensitiveFrameIsNeverInWhatPublishes()
    {
        // session.v1 says a sensitive_context frame is never sent or attached. It used to open included,
        // because the strip read excluded_by_user and nothing else, and went to the ticket with the rest.
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000), Frame("f2", 2000, sensitive: true)));

        Assert.False(strip.IsIncluded("f2"));
        Assert.Equal("1 of 2 included", strip.Header);
        Assert.Equal(["f1"], strip.ToPublish().Select(frame => frame.Id));
        Assert.True(strip.Frames.Single(cell => cell.Frame.Id == "f2").Withheld);
        Assert.False(strip.Frames.Single(cell => cell.Frame.Id == "f1").Withheld);
    }

    [Fact]
    public void ASensitiveFrameCannotBeSwitchedBackOn()
    {
        // The technician can take a frame out but cannot put this one in: Space answers with the state it
        // stays in, so the strip, the header and what publishes all keep agreeing.
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000, sensitive: true)));

        Assert.False(strip.Toggle("f1"));
        Assert.False(strip.Set("f1", included: true));
        Assert.False(strip.Toggle("f1"));

        Assert.False(strip.IsIncluded("f1"));
        Assert.False(strip.Frames.Single().Included);
        Assert.Empty(strip.ToPublish());
        Assert.Equal("0 of 1 included", strip.Header);
    }

    [Fact]
    public void ASensitiveFrameStaysOutWhenAnUndoneDeleteBringsItBack()
    {
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000, sensitive: true)));
        var removed = strip.Frames.Single().Frame;
        strip.Remove("f1");

        strip.Restore(removed, included: true);

        Assert.False(strip.IsIncluded("f1"));
        Assert.True(strip.Frames.Single().Withheld);
        Assert.Empty(strip.ToPublish());
    }

    [Fact]
    public void AFrameTakenWhileCaptureWasOffIsNeverInWhatPublishes()
    {
        // The same rule the drafting bundle applies (INV-6): something was suppressing capture when this
        // was taken, so the strip shows it withheld rather than offering it for the ticket.
        var session = WithFrames(Frame("f1", 1000), Frame("f2", 3000), Frame("f3", 5000)) with
        {
            Events =
            [
                new CaptureStateEvent { TsMs = 2000, State = CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField },
                new CaptureStateEvent { TsMs = 4000, State = CaptureState.Recording },
            ],
        };

        var strip = new Filmstrip(session);

        Assert.Equal(["f1", "f3"], strip.ToPublish().Select(frame => frame.Id));
        Assert.False(strip.Set("f2", included: true));
        Assert.Equal("2 of 3 included", strip.Header);
    }

    [Fact]
    public void AnOrdinaryFrameStillTogglesBothWays()
    {
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000), Frame("f2", 2000, sensitive: true)));

        Assert.False(strip.Toggle("f1"));
        Assert.True(strip.Toggle("f1"));
        Assert.Equal(["f1"], strip.ToPublish().Select(frame => frame.Id));
    }

    [Fact]
    public void AnInclusionTheServiceDidNotRecordGoesBackToExcluded()
    {
        // Publish attaches what the store says is included (ST-078). An inclusion that did not land would
        // not go out, so the strip must stop promising it.
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000), Frame("f2", 2000, excluded: true)));
        Assert.True(strip.Toggle("f2"));

        Assert.True(strip.Answered("f2", included: true, recorded: false));

        Assert.False(strip.IsIncluded("f2"));
        Assert.Equal(["f1"], strip.ToPublish().Select(frame => frame.Id));
        Assert.Equal("1 of 2 included", strip.Header);
    }

    [Fact]
    public void AnAnswerThatNeedsNoChangeLeavesTheStripAlone()
    {
        // A recorded write is already on screen. A failed exclusion stays out: the strip does not name the
        // frame at publish, so it is not sent either way. A frame that has left the strip is ignored.
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000), Frame("f2", 2000)));
        Assert.False(strip.Toggle("f2"));

        Assert.False(strip.Answered("f1", included: true, recorded: true));
        Assert.False(strip.Answered("f2", included: false, recorded: false));
        Assert.False(strip.Answered("gone", included: true, recorded: false));

        Assert.Equal(["f1"], strip.ToPublish().Select(frame => frame.Id));
    }

    [Fact]
    public void FramesAreNumberedByTheirPlaceInTime()
    {
        // The note's chips say "frame 2" and this is the 2. The store's order is the store's business.
        var strip = new Filmstrip(WithFrames(Frame("late", 3000), Frame("early", 1000), Frame("middle", 2000)));

        Assert.Equal(["frame 1", "frame 2", "frame 3"], strip.Frames.Select(cell => cell.Label));
        Assert.Equal(["early", "middle", "late"], strip.Frames.Select(cell => cell.Frame.Id));
    }

    [Fact]
    public void DeletingAFrameRenumbersTheRest()
    {
        // Otherwise the strip has a frame 1 and a frame 3, and the note's chips point at the wrong pictures.
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000), Frame("f2", 2000), Frame("f3", 3000)));

        strip.Remove("f2");

        Assert.Equal(["frame 1", "frame 2"], strip.Frames.Select(cell => cell.Label));
        Assert.Equal("2 of 2 included", strip.Header);
    }

    [Fact]
    public void AnUndoneDeleteComesBackWhereItWas()
    {
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000), Frame("f2", 2000), Frame("f3", 3000)));
        var removed = strip.Frames.Single(cell => cell.Frame.Id == "f2").Frame;
        strip.Remove("f2");

        strip.Restore(removed, included: true);

        Assert.Equal(["f1", "f2", "f3"], strip.Frames.Select(cell => cell.Frame.Id));
        Assert.Equal(["frame 1", "frame 2", "frame 3"], strip.Frames.Select(cell => cell.Label));
    }

    [Fact]
    public void ABlurredFrameKeepsItsPlaceAndWhetherItGoesOut()
    {
        // T-121. The frame a blur hands back carries the load-time excluded_by_user, which is stale once
        // the technician has pressed Space. Rebuilding the cell from the frame would put an excluded frame
        // back on the ticket; the cell keeps its own flag and only the picture changes.
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000), Frame("f2", 2000), Frame("f3", 3000)));
        Assert.False(strip.Toggle("f2"));
        var stale = strip.Frames.Single(cell => cell.Frame.Id == "f2").Frame;
        var blurred = stale with { MaskedRegions = [new MaskedRegion { X = 1, Y = 2, Width = 30, Height = 4, Kind = MaskKind.UserBlur }], OcrText = null };

        strip.Replace(blurred);

        var cell = strip.Frames.Single(c => c.Frame.Id == "f2");
        Assert.Same(blurred, cell.Frame);
        Assert.Equal(2, cell.Number);
        Assert.False(cell.Included);
        Assert.False(strip.IsIncluded("f2"));
        Assert.Equal(["f1", "f3"], strip.ToPublish().Select(frame => frame.Id));
        Assert.Equal("2 of 3 included", strip.Header);

        // And the other way: an included frame whose load-time flag says excluded stays included.
        var strip2 = new Filmstrip(WithFrames(Frame("g1", 1000, excluded: true)));
        Assert.True(strip2.Toggle("g1"));
        strip2.Replace(strip2.Frames.Single().Frame with { OcrText = null });
        Assert.True(strip2.IsIncluded("g1"));
        Assert.Equal(["g1"], strip2.ToPublish().Select(frame => frame.Id));
    }

    [Fact]
    public void AnUndoneDeleteOfAnExcludedFrameStaysExcluded()
    {
        // T-122. Excluded, deleted, then undeleted: the frame's own excluded_by_user is false, so a Restore
        // that trusted the frame rather than the argument would publish a frame the technician took out.
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000), Frame("f2", 2000)));
        Assert.False(strip.Toggle("f2"));
        var removed = strip.Frames.Single(cell => cell.Frame.Id == "f2").Frame;
        Assert.False(removed.ExcludedByUser);
        strip.Remove("f2");

        strip.Restore(removed, included: false);

        Assert.False(strip.IsIncluded("f2"));
        Assert.False(strip.Frames.Single(cell => cell.Frame.Id == "f2").Included);
        Assert.Equal(["f1"], strip.ToPublish().Select(frame => frame.Id));
        Assert.Equal("1 of 2 included", strip.Header);
        Assert.Equal(["frame 1", "frame 2"], strip.Frames.Select(cell => cell.Label));
    }

    [Fact]
    public void ASuppressedIntervalIsDrawnBetweenTheFramesEitherSideOfIt()
    {
        // Spec §5 S3. Without it the strip reads as though the session lost eighteen seconds, and the
        // technician cannot tell a suppression that worked from a capture that failed.
        var session = WithFrames(Frame("f1", 1000), Frame("f2", 5000)) with
        {
            Events =
            [
                new CaptureStateEvent { TsMs = 2000, State = CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField },
                new CaptureStateEvent { TsMs = 4000, State = CaptureState.Recording },
            ],
        };

        var strip = new Filmstrip(session);

        Assert.Collection(
            strip.Cells,
            cell => Assert.Equal("f1", Assert.IsType<FrameCell>(cell).Frame.Id),
            cell =>
            {
                var gap = Assert.IsType<GapCell>(cell);
                Assert.Equal(2000, gap.DurationMs);
                Assert.Equal("Paused — a password field had focus", gap.Description);
            },
            cell => Assert.Equal("f2", Assert.IsType<FrameCell>(cell).Frame.Id));
    }

    [Fact]
    public void ASuppressionThatNeverEndedIsStillDrawn()
    {
        // The session ended while suppressed. This is the case a technician most wants explained, and the
        // obvious loop - only emit on the closing event - is the one that drops it.
        var session = WithFrames(Frame("f1", 1000)) with
        {
            Events =
            [
                new CaptureStateEvent { TsMs = 2000, State = CaptureState.Suppressed, Reason = CaptureStateReason.ElevatedWindow },
                new ClickEvent { TsMs = 9000, X = 1, Y = 1, Button = MouseButton.Left },
            ],
        };

        var gap = Assert.Single(new Filmstrip(session).Cells.OfType<GapCell>());

        Assert.Equal(2000, gap.TsMs);
        Assert.Equal(9000, gap.EndMs);
    }

    [Fact]
    public void AZeroLengthSuppressionIsNotDrawn()
    {
        // It caught nothing, and a marker on the strip for a non-event is a question the technician has to
        // stop and answer.
        var session = WithFrames(Frame("f1", 1000)) with
        {
            Events =
            [
                new CaptureStateEvent { TsMs = 2000, State = CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField },
                new CaptureStateEvent { TsMs = 2000, State = CaptureState.Recording },
            ],
        };

        Assert.Empty(new Filmstrip(session).Cells.OfType<GapCell>());
    }

    [Fact]
    public void OverlappingSuppressionsAreOneGapNotThree()
    {
        // Two reasons can hold at once - an excluded app in front of a password field - and the state
        // machine reports each. Drawing one gap per event would put three markers on a single pause.
        var session = WithFrames(Frame("f1", 1000)) with
        {
            Events =
            [
                new CaptureStateEvent { TsMs = 2000, State = CaptureState.Suppressed, Reason = CaptureStateReason.ExcludedApp },
                new CaptureStateEvent { TsMs = 2500, State = CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField },
                new CaptureStateEvent { TsMs = 6000, State = CaptureState.Recording },
            ],
        };

        var gap = Assert.Single(new Filmstrip(session).Cells.OfType<GapCell>());

        Assert.Equal(2000, gap.TsMs);
        Assert.Equal(6000, gap.EndMs);

        // The first reason, because that is what stopped capture; the second only kept it stopped.
        Assert.Equal("Paused — an excluded app was in front", gap.Description);
    }

    [Fact]
    public void AKeystrokeAboutAFrameThatIsGoneIsIgnored()
    {
        var strip = new Filmstrip(WithFrames(Frame("f1", 1000)));

        Assert.Null(strip.Toggle("nonexistent"));
        Assert.Null(strip.Set("nonexistent", included: false));
        strip.Remove("nonexistent");

        Assert.Equal("1 of 1 included", strip.Header);
    }

    private static Session WithFrames(params Frame[] frames) => Session() with { Frames = frames };

    private static Frame Frame(string id, long tsMs, bool excluded = false, bool sensitive = false) => new()
    {
        Id = id,
        TsMs = tsMs,
        Trigger = FrameTrigger.Click,
        Image = $"{id}.png",
        Width = 1600,
        Height = 900,
        RedactionPending = false,
        RedactedAt = DateTimeOffset.UnixEpoch,
        MaskedRegions = [],
        SensitiveContext = sensitive,
        ExcludedByUser = excluded,
    };
}
