using ScreenTail.Core.Store;

namespace ScreenTail.Core.Capture;

/// <param name="SessionId">Which session it belongs to. The queue outlives no session, but it holds two.</param>
public readonly record struct QueuedFrame(string SessionId, StagedFrame Frame);

/// <summary>
/// Where a frame waits between being taken and being read (ADR-0006, ST-041).
///
/// It used to wait in the encrypted store with <c>redaction_pending = 1</c>. That was never an INV-1
/// breach — every read path filters the flag, and a row nothing can read is not shown, exported or
/// transmitted — but it was a window. Anything holding the store key could read the frame while it sat
/// there; a crash left it on disk until the next start, which may be days; and the freed page kept its
/// ciphertext until something reused it. Not writing the bytes closes all three at once, and closes them
/// by removing the thing to enforce against rather than by enforcing harder.
///
/// <b>The store was doing one useful thing, and this has to keep doing it.</b> A pending frame was never
/// kept for durability: recovery purges pending frames rather than resuming them, because nobody has
/// read them (ADR-0004). What the store provided was a buffer for when capture outruns redaction, and
/// disk could absorb a session's worth of that. Memory cannot — each frame is up to 1.5 MB and ST-031
/// budgets 600 MB for the whole application, most of which speech already claims.
///
/// So the question the old design answered by accident is answered here on purpose: <b>a frame that
/// finds the queue full is refused and counted.</b> Blocking the capture loop would put OCR back on the
/// 50 ms drain; falling back to the store would keep the window open for exactly the busy sessions where
/// it is widest. Refusing loses a screenshot and says so, through the same count that ADR-0004 needed
/// for a different reason.
/// </summary>
public sealed class PendingFrames(int depth = PendingFrames.DefaultDepth)
{
    /// <summary>
    /// How many frames may wait.
    ///
    /// Small on purpose. Redaction takes a median of 171 ms and a technician generates a few frames a
    /// minute, so a depth above one already means redaction is losing; this is here to absorb a scene
    /// change landing on the same second as a click, not to hold a session.
    /// </summary>
    public const int DefaultDepth = 4;

    private readonly Lock _gate = new();
    private readonly Queue<QueuedFrame> _waiting = new();

    /// <summary>
    /// Taken by a worker and not yet written.
    ///
    /// The store used to track this by leaving the row <c>redaction_pending</c> until the redaction
    /// finished, which is also why the worker kept its own set of ids: the query could not tell
    /// "waiting" from "being worked on", so two workers took the same row. Owning both states here
    /// answers that question once, and lets finalize ask a single thing — is anything of this session
    /// still unwritten?
    /// </summary>
    private readonly List<QueuedFrame> _inFlight = [];
    private readonly int _depth = depth > 0 ? depth : throw new ArgumentOutOfRangeException(nameof(depth));
    private TaskCompletionSource _arrived = Fresh();
    private long _dropped;

    /// <summary>
    /// How many frames are unwritten: waiting, plus taken and not yet finished.
    ///
    /// The HUD's backlog, which used to be a query. Both states count, because both mean a frame that
    /// has not reached the store and that finalize must wait for.
    /// </summary>
    public int Depth
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count + _inFlight.Count;
            }
        }
    }

    /// <summary>The same, for one session. What finalize waits on before it purges and draws a line.</summary>
    public int DepthFor(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_gate)
        {
            return _waiting.Count(queued => Same(queued, sessionId)) + _inFlight.Count(queued => Same(queued, sessionId));
        }
    }

    /// <summary>
    /// How many frames were refused for want of room, for the life of this service.
    ///
    /// Counted rather than logged: a number belongs in the bundle, where it makes the draft hedge
    /// instead of claiming to have seen a session it only partly saw (INV-10, ADR-0004).
    /// </summary>
    public long Dropped
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>
    /// Puts a frame in, or refuses it because there is no room.
    ///
    /// <b>The oldest is kept and the newest refused</b>, rather than evicting to make space. A frame
    /// already waiting has been counted as captured and may already be being read; dropping it would
    /// mean a frame that was reported and then vanished. The one that never got in is the only one
    /// nobody has promised anything about yet.
    /// </summary>
    public bool TryEnqueue(string sessionId, StagedFrame frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(frame);

        TaskCompletionSource woken;
        lock (_gate)
        {
            if (_waiting.Count >= _depth)
            {
                _dropped++;
                return false;
            }

            _waiting.Enqueue(new QueuedFrame(sessionId, frame));
            woken = _arrived;
            _arrived = Fresh();
        }

        // Outside the lock: a continuation that ran inline would run holding it.
        woken.TrySetResult();
        return true;
    }

    /// <summary>
    /// Takes the oldest, or says there is nothing rather than waiting for one.
    ///
    /// The frame moves to in-flight rather than leaving: it is still unwritten, and finalize must still
    /// wait for it. The caller says it is finished with <see cref="Done"/>, whether the frame was
    /// written or discarded — both are outcomes, and neither leaves it owed.
    /// </summary>
    public bool TryTake(out QueuedFrame frame)
    {
        lock (_gate)
        {
            if (!_waiting.TryDequeue(out frame))
            {
                return false;
            }

            _inFlight.Add(frame);
            return true;
        }
    }

    /// <summary>
    /// This frame is no longer owed, however it ended.
    ///
    /// Called in a finally, because a frame left in flight by a throw would hold finalize open for the
    /// whole redaction grace and then be purged anyway.
    /// </summary>
    public void Done(QueuedFrame frame)
    {
        lock (_gate)
        {
            var at = _inFlight.FindIndex(queued => string.Equals(queued.Frame.Id, frame.Frame.Id, StringComparison.Ordinal));
            if (at >= 0)
            {
                _inFlight.RemoveAt(at);
            }
        }
    }

    /// <summary>
    /// Completes when there may be something to take.
    ///
    /// "May": two workers can be woken by one frame and only one of them gets it, which is why the
    /// caller loops on <see cref="TryTake"/> rather than trusting this.
    /// </summary>
    public Task WaitToTakeAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return _waiting.Count > 0 ? Task.CompletedTask : _arrived.Task.WaitAsync(ct);
        }
    }

    /// <summary>
    /// Throws away whatever is waiting for one session.
    ///
    /// The technician discarded it, so the frames still queued go with it. They have never been read, so
    /// nobody can say what is on them — which is the same reason ADR-0004 gives for discarding rather
    /// than storing an unread frame.
    /// </summary>
    /// <returns>How many were thrown away, so the caller can count the loss against the session.</returns>
    public int Forget(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_gate)
        {
            if (_waiting.Count == 0)
            {
                return 0;
            }

            var kept = _waiting.Where(queued => !Same(queued, sessionId)).ToList();
            var dropped = _waiting.Count - kept.Count;
            _waiting.Clear();
            foreach (var frame in kept)
            {
                _waiting.Enqueue(frame);
            }

            return dropped;
        }
    }

    private static bool Same(QueuedFrame queued, string sessionId) =>
        string.Equals(queued.SessionId, sessionId, StringComparison.Ordinal);

    private static TaskCompletionSource Fresh() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
