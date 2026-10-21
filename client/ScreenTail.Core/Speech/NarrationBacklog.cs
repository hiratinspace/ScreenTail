namespace ScreenTail.Core.Speech;

/// <summary>
/// What narration each session is still owed: segments cut from the microphone while it was recording and
/// not yet written or refused (ST-027).
///
/// The recorder adds to it as it queues a segment and settles as each one finishes. The session machine
/// reads it at finalize, for the same reason it reads <see cref="Capture.PendingFrames"/>: the model takes
/// seconds per segment, so the last few things a technician said are usually still being transcribed when
/// they press Stop. Without the wait those segments arrived after the session had stopped taking them and
/// the end of every busy session was lost, and that end is usually the outcome (2026-10-12 review).
///
/// <b>Forgetting a session is how its leftovers are dropped.</b> Once a session is discarded or finalized,
/// what is still queued for it is never transcribed: the model's time is not spent on words nobody can
/// store, and the copy of the customer's call that each queued segment holds is let go of sooner.
///
/// <b>A forgotten session stays forgotten.</b> The recorder asks which session it is hearing and queues what
/// it heard a moment later, and a finalize or a discard can fall between the two. A segment owed to a
/// session already let go of is not counted, so it is neither waited for nor transcribed.
///
/// Small by construction. The recorder's queue holds four segments and the model one more, so there are
/// never more than a handful of entries, and an entry is removed when its count reaches zero. Only the
/// last <see cref="Remembered"/> finished sessions are kept, which is far more than can end while one
/// chunk of audio is between the question and the queue.
/// </summary>
public sealed class NarrationBacklog
{
    private const int Remembered = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _owed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private readonly Queue<string> _finishedInOrder = new(Remembered);

    /// <summary>A segment heard in this session is waiting for the model. Not counted once the session is forgotten.</summary>
    public void Owe(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_gate)
        {
            if (_finished.Contains(sessionId))
            {
                return;
            }

            _owed[sessionId] = _owed.GetValueOrDefault(sessionId) + 1;
        }
    }

    /// <summary>One of them was written, refused or given up on. Nothing happens for a session already forgotten.</summary>
    public void Settle(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_gate)
        {
            if (!_owed.TryGetValue(sessionId, out var owed))
            {
                return;
            }

            if (owed <= 1)
            {
                _ = _owed.Remove(sessionId);
            }
            else
            {
                _owed[sessionId] = owed - 1;
            }
        }
    }

    /// <summary>How many segments this session is still owed. What finalize waits on.</summary>
    public int DepthFor(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_gate)
        {
            return _owed.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>
    /// Whether a queued segment of this session is still worth transcribing. False once the session has
    /// been forgotten, which is what discarding or finalizing it does.
    /// </summary>
    public bool Wants(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_gate)
        {
            return _owed.ContainsKey(sessionId);
        }
    }

    /// <summary>The session is over: whatever is still queued for it is dropped. Returns how many that was.</summary>
    public int Forget(string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        lock (_gate)
        {
            if (_finished.Add(sessionId))
            {
                _finishedInOrder.Enqueue(sessionId);
                if (_finishedInOrder.Count > Remembered)
                {
                    _ = _finished.Remove(_finishedInOrder.Dequeue());
                }
            }

            return _owed.Remove(sessionId, out var owed) ? owed : 0;
        }
    }
}
