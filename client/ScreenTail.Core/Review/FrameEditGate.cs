namespace ScreenTail.Core.Review;

/// <summary>
/// One turn at a time between the edits that destroy pixels and the reads that send them away, in the
/// service (ST-084 follow-up review of #257).
///
/// A blur reads a frame, paints it and writes it back; a publish reads the frames' bytes and sends them.
/// A publish that read a frame while a blur of it was being painted would send the picture the
/// technician had just asked to cover. With the two sharing this gate, a publish waits for a blur that is
/// already under way and then reads what it wrote. The pane holds Publish back while it waits for a blur
/// too; this is the service's half, so neither side alone is the only guarantee.
/// </summary>
#pragma warning disable CA1001 // The semaphore never hands out its wait handle, so there is nothing for a Dispose to release.
public sealed class FrameEditGate
#pragma warning restore CA1001
{
    private readonly SemaphoreSlim _turn = new(1, 1);

    /// <summary>Waits for the turn; disposing the result gives it back.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        return new Turn(_turn);
    }

    /// <summary>Whether someone holds the turn now. For tests.</summary>
    public bool IsHeld => _turn.CurrentCount == 0;

    private sealed class Turn(SemaphoreSlim turn) : IDisposable
    {
        private SemaphoreSlim? _turn = turn;

        public void Dispose() => Interlocked.Exchange(ref _turn, null)?.Release();
    }
}
