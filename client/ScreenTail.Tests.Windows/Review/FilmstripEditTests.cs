using System.Runtime.Versioning;
using ScreenTail.UI.Review;
using static ScreenTail.Tests.Windows.Review.ReviewSessions;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// What the filmstrip tells the service, and when (ST-075; 2026-10-12 review S-35, T-136, E-105, T-237,
/// E-30). A delete the technician was told is permanent must either happen or be said not to have; a
/// frame they deleted must not go out if the process dies inside the undo window; and loading thumbnails
/// must survive the strip changing under it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FilmstripEditTests
{
    [Fact]
    public async Task ClosingTheReviewCommitsAPendingDeleteAndARefusalIsRecorded()
    {
        // T-136. DeleteAsync answers false when the service refused or the pipe was down. The old code
        // ignored it, so the frame came back included next time and nothing on screen said so.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames { Deletes = false };
        var strip = new FilmstripViewModel(Session(Frames(2)), frames);
        var first = strip.Items.OfType<FrameItem>().First();

        await strip.DeleteCommand.ExecuteAsync(first);
        Assert.Empty(frames.Deleted);
        Assert.Null(strip.EditError);

        strip.Dispose();

        Assert.Equal(["f1"], frames.Deleted);
        Assert.True(strip.HasEditError);
        Assert.Contains("frame 1", strip.EditError, StringComparison.Ordinal);
        Assert.Equal(["f2"], strip.IncludedFrameIds);
    }

    [Fact]
    public async Task ADeleteTheServiceRecordedLeavesNothingToReport()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames();
        var strip = new FilmstripViewModel(Session(Frames(2)), frames);

        await strip.DeleteCommand.ExecuteAsync(strip.Items.OfType<FrameItem>().First());
        strip.Dispose();

        Assert.Equal(["f1"], frames.Deleted);
        Assert.Null(strip.EditError);
        Assert.False(strip.HasEditError);
    }

    [Fact]
    public async Task PressingDelExcludesTheFrameAtOnceAndUndoPutsItBack()
    {
        // S-35. The row is deleted only when the undo window closes, so a crash or a down pipe inside those
        // five seconds used to leave the frame included in the store, and it went out with the next
        // publish. Now Del writes the exclude first; the delete follows when the window closes.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames();
        var strip = new FilmstripViewModel(Session(Frames(2)), frames);
        var first = strip.Items.OfType<FrameItem>().First();

        await strip.DeleteCommand.ExecuteAsync(first);

        Assert.Equal([("f1", false)], frames.Included);
        Assert.Equal(["f2"], strip.IncludedFrameIds);
        Assert.True(strip.HasUndoToast);

        await strip.UndoCommand.ExecuteAsync(null);

        Assert.Equal([("f1", false), ("f1", true)], frames.Included);
        Assert.True(first.Included);
        Assert.Equal("included", first.StateLabel);
        Assert.Equal(["f1", "f2"], strip.IncludedFrameIds);
        Assert.Equal("2 of 2 included", strip.Header);
        Assert.Empty(frames.Deleted);
        Assert.False(strip.HasUndoToast);
    }

    [Fact]
    public async Task AnExclusionTheServiceDidNotRecordStaysOutAndIsSaid()
    {
        // S-35. A refused Space used to be invisible: the strip showed the frame excluded, the store kept it
        // included, and it was back the next time the session opened. It stays out of this publish, and
        // the pane says the store disagrees.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames { Records = false };
        var strip = new FilmstripViewModel(Session(Frames(2)), frames);
        var first = strip.Items.OfType<FrameItem>().First();

        await strip.ToggleIncludeCommand.ExecuteAsync(first);

        Assert.Equal([("f1", false)], frames.Included);
        Assert.False(first.Included);
        Assert.Equal(["f2"], strip.IncludedFrameIds);
        Assert.True(strip.HasEditError);
        Assert.Contains("frame 1", strip.EditError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADelWhoseExclusionWasNotRecordedIsSaidAndTheNextEditClearsIt()
    {
        // Del writes the exclusion at once (S-35). A refusal there is the same news as a refused Space:
        // if the delete never lands either, the frame is back, included, next time.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames { Records = false };
        var strip = new FilmstripViewModel(Session(Frames(3)), frames);
        var items = strip.Items.OfType<FrameItem>().ToList();

        await strip.DeleteCommand.ExecuteAsync(items[0]);

        Assert.Equal([("f1", false)], frames.Included);
        Assert.True(strip.HasEditError);
        Assert.Contains("frame 1", strip.EditError, StringComparison.Ordinal);

        frames.Records = true;
        await strip.ToggleIncludeCommand.ExecuteAsync(items[1]);

        Assert.False(strip.HasEditError);
        Assert.Null(strip.EditError);
    }

    [Fact]
    public async Task AnEditThatLandsClearsTheLastRefusal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames { Records = false };
        var strip = new FilmstripViewModel(Session(Frames(2)), frames);
        var first = strip.Items.OfType<FrameItem>().First();

        await strip.ToggleIncludeCommand.ExecuteAsync(first);
        Assert.True(strip.HasEditError);

        frames.Records = true;
        await strip.ToggleIncludeCommand.ExecuteAsync(first);

        Assert.True(first.Included);
        Assert.False(strip.HasEditError);
    }

    [Fact]
    public async Task LeavingReviewDoesNotWaitForeverOnADeleteTheServiceNeverAnswers()
    {
        // DisposeAsync waits for the permanent delete's answer, but only so long: the pipe may be stuck
        // behind a slow publish, and leaving Review must not hang on it. The wait is bounded; here the
        // bound is zero, so the test needs no clock.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames { DeleteAnswer = new TaskCompletionSource<bool>() };
        var strip = new FilmstripViewModel(Session(Frames(2)), frames, answerWait: TimeSpan.Zero);
        await strip.DeleteCommand.ExecuteAsync(strip.Items.OfType<FrameItem>().First());

        var leaving = strip.DisposeAsync();

        Assert.True(leaving.IsCompleted, "leaving Review waited on a delete the service never answered");
        await leaving;
        Assert.Equal(["f1"], frames.Deleted);
        frames.DeleteAnswer.SetResult(true);
    }

    [Fact]
    public async Task DeletingAnExcludedFrameWritesNothingAndUndoKeepsItExcluded()
    {
        // Already out, so there is nothing to persist, and an undo must not quietly put it back in.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames();
        var strip = new FilmstripViewModel(Session(Frame("f1", 1000, excluded: true), Frame("f2", 2000)), frames);
        var first = strip.Items.OfType<FrameItem>().First();

        await strip.DeleteCommand.ExecuteAsync(first);
        await strip.UndoCommand.ExecuteAsync(null);

        Assert.Empty(frames.Included);
        Assert.False(first.Included);
        Assert.Equal(["f2"], strip.IncludedFrameIds);
    }

    [Fact]
    public async Task AnUndoWhoseReinclusionWasNotRecordedShowsTheFrameExcluded()
    {
        // The same rule as Space (ST-078): publish attaches what the store says, so an inclusion the
        // service did not record is shown as excluded rather than promised.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames();
        var strip = new FilmstripViewModel(Session(Frames(2)), frames);
        var first = strip.Items.OfType<FrameItem>().First();
        await strip.DeleteCommand.ExecuteAsync(first);

        frames.Records = false;
        await strip.UndoCommand.ExecuteAsync(null);

        Assert.Equal([("f1", false), ("f1", true)], frames.Included);
        Assert.Contains(first, strip.Items);
        Assert.False(first.Included);
        Assert.Equal("excluded", first.StateLabel);
        Assert.Equal(["f2"], strip.IncludedFrameIds);
        Assert.Equal("1 of 2 included", strip.Header);
    }

    [Fact]
    public async Task DeletingAFrameWhileThumbnailsLoadDoesNotThrow()
    {
        // T-237, E-105. LoadImagesAsync used to enumerate the live Items across its awaits; a Del in the
        // middle threw "Collection was modified" out of an async-void Loaded handler and took the UI
        // process, and the staged delete, with it.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var strip = new FilmstripViewModel(Session(Frames(3)), frames);
        var items = strip.Items.OfType<FrameItem>().ToList();
        var loading = strip.LoadImagesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["f1"], frames.ThumbnailsAsked);

        await strip.DeleteCommand.ExecuteAsync(items[1]);
        frames.Gate.SetResult();
        await loading;

        Assert.Equal(["f1", "f3"], strip.Items.OfType<FrameItem>().Select(item => item.Id));
        Assert.NotNull(items[0].Thumbnail);
        Assert.NotNull(items[2].Thumbnail);

        // The deleted frame's picture is not pulled over the pipe for nobody.
        Assert.Equal(["f1", "f3"], frames.ThumbnailsAsked);
    }

    [Fact]
    public async Task UndoingADeleteWhileThumbnailsLoadDoesNotThrowAndTheFrameGetsItsPicture()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var strip = new FilmstripViewModel(Session(Frames(3)), frames);
        var items = strip.Items.OfType<FrameItem>().ToList();
        await strip.DeleteCommand.ExecuteAsync(items[1]);
        var loading = strip.LoadImagesAsync(TestContext.Current.CancellationToken);

        var undo = strip.UndoCommand.ExecuteAsync(null);
        frames.Gate.SetResult();
        await loading;
        await undo;

        Assert.Equal(["f1", "f2", "f3"], strip.Items.OfType<FrameItem>().Select(item => item.Id));
        Assert.All(items, item => Assert.NotNull(item.Thumbnail));
    }

    [Fact]
    public async Task ClosingThePaneStopsTheThumbnailLoad()
    {
        // E-105. The pane's Unloaded cancels the token; the load stops at the frame it was on rather than
        // pulling the rest of the session over the shared pipe for a view that is gone.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var strip = new FilmstripViewModel(Session(Frames(5)), frames);
        using var closing = new CancellationTokenSource();
        var loading = strip.LoadImagesAsync(closing.Token);

        await closing.CancelAsync();
        frames.Gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Equal(["f1"], frames.ThumbnailsAsked);
        Assert.All(strip.Items.OfType<FrameItem>(), item => Assert.Null(item.Thumbnail));
    }

    [Fact]
    public async Task TheStripAsksForThumbnailsAndOnlyEnlargeAsksForTheFullImage()
    {
        // E-30. Drawing 160 px cells from full redacted JPEGs cost tens of megabytes on the pipe per Review
        // open. The strip asks for the small picture; the full one is fetched for the enlarged frame alone.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames();
        var strip = new FilmstripViewModel(Session(Frames(3)), frames);

        await strip.LoadImagesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["f1", "f2", "f3"], frames.ThumbnailsAsked);
        Assert.Empty(frames.ImagesAsked);
        Assert.All(strip.Items.OfType<FrameItem>(), item => Assert.NotNull(item.Thumbnail));

        await strip.EnlargeCommand.ExecuteAsync(strip.Items.OfType<FrameItem>().First());

        Assert.Equal(["f1"], frames.ImagesAsked);
    }

    [Fact]
    public async Task AFrameWhosePictureCannotBeDecodedLeavesTheRestOfTheStripAlone()
    {
        // Bytes that are not an image must not take the Loaded handler, and the UI, down with them.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames { Image = [1, 2, 3] };
        var strip = new FilmstripViewModel(Session(Frames(2)), frames);

        await strip.LoadImagesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["f1", "f2"], frames.ThumbnailsAsked);
        Assert.All(strip.Items.OfType<FrameItem>(), item => Assert.Null(item.Thumbnail));
    }
}
