using ScreenTail.Core.Capture;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Capture;

/// <summary>
/// Where a frame waits between being taken and being read (ADR-0006, ST-041).
///
/// It used to wait in the encrypted store, with <c>redaction_pending = 1</c>. That was never an INV-1
/// breach — every read path filters the flag — but it was a window: anything holding the store key could
/// read it while it sat there, a crash left it on disk until the next start, and the freed page kept its
/// ciphertext until something reused it. ADR-0006 closes all three by not writing the bytes at all.
///
/// The store was doing one useful thing for those frames, and this has to keep doing it: absorbing a
/// burst when capture outruns redaction. Disk could take a session's worth; memory cannot, so the
/// question the old design answered by accident has to be answered on purpose here.
/// </summary>
public sealed class PendingFramesTests
{
    [Fact]
    public void AFrameGoesInAndComesOutUnchanged()
    {
        var queue = new PendingFrames();

        Assert.True(queue.TryEnqueue("s1", Frame("f1")));

        Assert.True(queue.TryTake(out var taken));
        Assert.Equal("s1", taken.SessionId);
        Assert.Equal("f1", taken.Frame.Id);
    }

    [Fact]
    public void FramesComeBackInTheOrderTheyWereTaken()
    {
        // A session's timeline is its order. Two workers take from here, and a queue that handed them
        // out backwards would put the fix before the fault in the note.
        var queue = new PendingFrames();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(queue.TryEnqueue("s1", Frame($"f{i}")));
        }

        var order = new List<string>();
        while (queue.TryTake(out var taken))
        {
            order.Add(taken.Frame.Id);
        }

        Assert.Equal(["f0", "f1", "f2", "f3"], order);
    }

    [Fact]
    public void AFullQueueRefusesRatherThanGrowing()
    {
        // The decision ADR-0006 exists to make. Each frame is up to 1.5 MB and ST-031 budgets 600 MB for
        // everything, most of which speech already has; a queue that grew would be a session's
        // screenshots in memory.
        var queue = new PendingFrames(depth: 2);

        Assert.True(queue.TryEnqueue("s1", Frame("f1")));
        Assert.True(queue.TryEnqueue("s1", Frame("f2")));

        Assert.False(queue.TryEnqueue("s1", Frame("f3")));
        Assert.Equal(2, queue.Depth);
    }

    [Fact]
    public void TheOldestIsKeptAndTheNewestRefused()
    {
        // Rather than evicting to make room. A frame already in the queue has been counted as captured
        // and may already be being read; dropping it would mean a frame that was reported and then
        // vanished. The one that never got in is the one nobody has promised anything about.
        var queue = new PendingFrames(depth: 1);
        Assert.True(queue.TryEnqueue("s1", Frame("first")));
        Assert.False(queue.TryEnqueue("s1", Frame("second")));

        Assert.True(queue.TryTake(out var taken));
        Assert.Equal("first", taken.Frame.Id);
    }

    [Fact]
    public void WhatWasRefusedIsCounted()
    {
        // A dropped frame is a hole in what the note was written from, and ADR-0004 already established
        // that such a hole is said out loud rather than left silent.
        var queue = new PendingFrames(depth: 1);
        _ = queue.TryEnqueue("s1", Frame("f1"));
        _ = queue.TryEnqueue("s1", Frame("f2"));
        _ = queue.TryEnqueue("s1", Frame("f3"));

        Assert.Equal(2, queue.Dropped);
    }

    [Fact]
    public void AnEmptyQueueSaysSoRatherThanWaiting()
    {
        // The worker polls; it must not block a thread on an empty queue for the life of a quiet session.
        var queue = new PendingFrames();

        Assert.False(queue.TryTake(out _));
    }

    [Fact]
    public async Task AWorkerWaitingIsWokenByAFrameArriving()
    {
        // The other half: a worker that polls an empty queue on a timer adds latency to every frame.
        var queue = new PendingFrames();
        var waiting = queue.WaitToTakeAsync(TestContext.Current.CancellationToken);

        Assert.True(queue.TryEnqueue("s1", Frame("f1")));

        // Completing at all is the claim: a worker that was not woken would hang here instead.
        await waiting.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(queue.TryTake(out _));
    }

    [Fact]
    public void ADiscardedSessionLeavesNothingBehind()
    {
        // The technician threw the session away, so the frames waiting to be read go with it. They have
        // never been read, so nobody can say what is on them, and ADR-0004 is clear about such a frame.
        var queue = new PendingFrames();
        _ = queue.TryEnqueue("keep", Frame("a"));
        _ = queue.TryEnqueue("drop", Frame("b"));
        _ = queue.TryEnqueue("keep", Frame("c"));

        queue.Forget("drop");

        var left = new List<string>();
        while (queue.TryTake(out var taken))
        {
            left.Add(taken.Frame.Id);
        }

        Assert.Equal(["a", "c"], left);
    }

    [Fact]
    public void AFrameTakenIsStillOwedUntilItIsDone()
    {
        // The state the store used to hold by leaving the row pending until redaction finished. Finalize
        // waits on this: a frame taken and not yet written would otherwise land after the session was
        // closed and drawn a line under.
        var queue = new PendingFrames();
        _ = queue.TryEnqueue("s1", Frame("f1"));

        Assert.True(queue.TryTake(out var taken));
        Assert.Equal(1, queue.Depth);

        queue.Done(taken);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public void OneSessionsBacklogIsNotAnothers()
    {
        // Two sessions never overlap today, but finalize asks about one of them by name and a count
        // that included the other would hold it open for a frame it has nothing to do with.
        var queue = new PendingFrames();
        _ = queue.TryEnqueue("mine", Frame("a"));
        _ = queue.TryEnqueue("theirs", Frame("b"));

        Assert.Equal(1, queue.DepthFor("mine"));
        Assert.Equal(2, queue.Depth);
    }

    [Fact]
    public void AFrameFinishedTwiceIsNotCountedTwice()
    {
        // Done is called from a finally and the success path also reaches it on some routes. Making it
        // idempotent is cheaper than reasoning about which.
        var queue = new PendingFrames();
        _ = queue.TryEnqueue("s1", Frame("f1"));
        Assert.True(queue.TryTake(out var taken));

        queue.Done(taken);
        queue.Done(taken);

        Assert.Equal(0, queue.Depth);
    }

    private static StagedFrame Frame(string id) =>
        new(id, 1_000, FrameTrigger.Click, 1920, 1080, null, new byte[] { 0xAA });
}
