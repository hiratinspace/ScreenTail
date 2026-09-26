using System.Runtime.Versioning;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Review;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// The timeline panel's view (ST-076, Spec §5 S3), over the model <c>TimelineTests</c> already covers:
/// a transcript line selects its frame in the filmstrip, a line with no frame selects nothing, Alt+T
/// toggles and the choice is handed back to be saved, and a note's frame chip reaches the same frame.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TimelineViewModelTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 18, 14, 2, 0, TimeSpan.Zero);

    [Fact]
    public void ClickingATranscriptLineSelectsItsFrameInTheFilmstrip()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var review = new ReviewViewModel(Session(), frames: null);

        review.Timeline.SelectedLine = review.Timeline.Lines.Single(l => l.Id == "t1");

        Assert.Equal("f1", Assert.IsType<FrameItem>(review.Strip.Selected).Id);
    }

    [Fact]
    public void ALineWithNoScreenshotSelectsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var review = new ReviewViewModel(Session(), frames: null);
        review.Strip.Select("f1");

        review.Timeline.SelectedLine = review.Timeline.Lines.Single(l => l.Id == "t2");

        Assert.Null(review.Strip.Selected);
    }

    [Fact]
    public void AltTTogglesThePanelAndHandsTheChoiceBackToBeSaved()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var saved = new List<bool>();
        var review = new ReviewViewModel(Session(), frames: null, timelineExpanded: false, timelineToggled: saved.Add);
        Assert.Equal(SessionTimeline.CollapsedHeight, review.Timeline.Height);

        review.Timeline.ToggleCommand.Execute(null);

        Assert.True(review.Timeline.Expanded);
        Assert.Equal(SessionTimeline.ExpandedHeight, review.Timeline.Height);
        Assert.Equal([true], saved);
    }

    [Fact]
    public void AFrameChipSelectsTheFrameAndAnUnknownIdSelectsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var review = new ReviewViewModel(Session(), frames: null);

        Assert.True(review.Strip.Select("f1"));
        Assert.Equal("f1", Assert.IsType<FrameItem>(review.Strip.Selected).Id);
        Assert.False(review.Strip.Select("nope"));
        Assert.Null(review.Strip.Selected);
    }

    [Fact]
    public void MarkersSayWhenAndWhatForTheTrack()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var review = new ReviewViewModel(Session(), frames: null);

        var frame = review.Timeline.Markers.Single(m => m.Kind == TimelineMarkerKind.Frame);
        var band = review.Timeline.Markers.Single(m => m.Kind == TimelineMarkerKind.Suppressed);

        Assert.Equal("f1", frame.FrameId);
        Assert.Contains("0:10", frame.Tooltip, StringComparison.Ordinal);
        Assert.True(band.IsBand);
        Assert.Contains("0:20", band.Tooltip, StringComparison.Ordinal);
        Assert.Contains("0:30", band.Tooltip, StringComparison.Ordinal);
        Assert.Contains("password", band.Tooltip, StringComparison.OrdinalIgnoreCase);
    }

    private static Session Session() => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s1",
        StartedAt = Started,
        DurationMs = 60_000,
        RemoteTool = new RemoteTool { Kind = RemoteToolKind.Screenconnect },
        PartialCapture = false,
        FramesPurgedUnredacted = 0,
        LocalOnly = false,
        Events =
        [
            new CaptureStateEvent { TsMs = 20_000, State = CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField },
            new CaptureStateEvent { TsMs = 30_000, State = CaptureState.Recording },
        ],
        Frames =
        [
            new Frame
            {
                Id = "f1",
                TsMs = 10_000,
                Trigger = FrameTrigger.Click,
                Image = "aW1hZ2U=",
                Width = 1920,
                Height = 1080,
                RedactionPending = false,
                RedactedAt = Started,
                MaskedRegions = [],
                SensitiveContext = false,
                ExcludedByUser = false,
            },
        ],
        Transcript =
        [
            new TranscriptSegment { Id = "t1", TsMs = 9_500, EndMs = 12_000, Speaker = Speaker.Tech, Text = "clearing the queue now", Confidence = 0.9, FrameId = "f1" },
            new TranscriptSegment { Id = "t2", TsMs = 40_000, EndMs = 42_000, Speaker = Speaker.Tech, Text = "just looking at the event log", Confidence = 0.9 },
        ],
    };
}
