using ScreenTail.Core.Review;

namespace ScreenTail.Tests.Review;

/// <summary>ST-075: the five seconds between a destructive act and it being permanent (Spec §4, §5 S3).</summary>
public sealed class UndoWindowTests
{
    private readonly ManualTime _clock = new(DateTimeOffset.UnixEpoch);
    private readonly List<string> _committed = [];
    private readonly List<string> _undone = [];

    [Fact]
    public void UndoingInsideTheWindowPutsItBack()
    {
        var window = Window();
        window.Stage("frame 2");

        _clock.Advance(TimeSpan.FromSeconds(4));
        window.Tick();

        Assert.True(window.Undo());
        Assert.Equal(["frame 2"], _undone);
        Assert.Empty(_committed);
    }

    [Fact]
    public void AfterFiveSecondsItIsGone()
    {
        // The AC's "then permanent". The commit is what makes the bytes unrecoverable, and until it runs
        // nothing has actually been destroyed - so a window that never closes is a delete that never
        // happens, and the technician's Delete key did nothing.
        var window = Window();
        window.Stage("frame 2");

        _clock.Advance(TimeSpan.FromSeconds(5));
        window.Tick();

        Assert.Equal(["frame 2"], _committed);
        Assert.False(window.Undo());
        Assert.Empty(_undone);
    }

    [Fact]
    public void TickingWithNothingStagedDoesNothingAtAll()
    {
        // It runs on the pane's timer, four times a second, for as long as Review is open.
        var window = Window();

        for (var i = 0; i < 100; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            window.Tick();
        }

        Assert.Empty(_committed);
        Assert.Empty(_undone);
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void DeletingASecondFrameCommitsTheFirstRatherThanLosingIt()
    {
        // A technician clearing four frames in a row. Replacing the held item would leave three of them
        // unrecoverable with no toast ever saying so - the undo silently applying to only the last one.
        var window = Window();
        window.Stage("frame 1");

        window.Stage("frame 2");

        Assert.Equal(["frame 1"], _committed);
        Assert.True(window.Undo());
        Assert.Equal(["frame 2"], _undone);
    }

    [Fact]
    public void LosingFocusClosesTheWindowNow()
    {
        // Spec §5 S3 ends the blur's undo when the pane loses focus. A held original that outlives the
        // screen it belongs to is a copy of something a technician asked to destroy.
        var window = Window();
        window.Stage("the original pixels");

        window.Close();

        Assert.Equal(["the original pixels"], _committed);
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void ClosingTwiceCommitsOnce()
    {
        // The pane loses focus and is then closed. Committing twice would delete a frame that the second
        // call no longer has any business touching - by then the id may belong to nothing.
        var window = Window();
        window.Stage("frame 2");

        window.Close();
        window.Close();
        window.Tick();

        Assert.Equal(["frame 2"], _committed);
    }

    [Fact]
    public void WhatTheToastSaysItWillUndo()
    {
        var window = Window();
        Assert.Null(window.Pending);

        window.Stage("frame 2");

        Assert.Equal("frame 2", window.Pending);
        Assert.True(window.IsOpen);
    }

    private UndoWindow<string> Window() => new(
        _committed.Add,
        _undone.Add,
        TimeSpan.FromSeconds(5),
        _clock);
}
