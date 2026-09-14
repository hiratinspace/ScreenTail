using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>One thing in the strip, in time order: either a screenshot or a stretch with none.</summary>
public abstract record FilmstripCell(long TsMs);

/// <param name="Number">What the technician reads — the frame's place in the strip, counted from one.
/// The note's chips say "frame 2" and this is the 2.</param>
/// <param name="Included">Whether it goes out when the note is published.</param>
public sealed record FrameCell(Frame Frame, int Number, bool Included) : FilmstripCell(Frame.TsMs)
{
    public string Label => $"frame {Number}";
}

/// <summary>
/// A stretch of the session with no screenshots because capture was deliberately off (INV-6), drawn as a
/// striped gap the width of a frame.
///
/// Spec §5 S3 asks for this and it is not decoration. Without it the strip reads as though the session
/// simply lost eighteen seconds, and the technician has no way to tell a suppression that worked from a
/// capture that failed — which is the difference between the product doing its job and the product
/// having a bug.
/// </summary>
public sealed record GapCell(long TsMs, long EndMs, CaptureStateReason? Reason) : FilmstripCell(TsMs)
{
    public long DurationMs => EndMs - TsMs;

    /// <summary>The tooltip. Says what stopped capture, in the words a technician would use.</summary>
    public string Description => Reason switch
    {
        CaptureStateReason.PasswordField => "Paused — a password field had focus",
        CaptureStateReason.ExcludedApp => "Paused — an excluded app was in front",
        CaptureStateReason.ElevatedWindow => "Paused — an elevated window had focus",
        CaptureStateReason.SensitiveContext => "Paused — the screen looked sensitive",
        CaptureStateReason.OutOfScope => "Not captured — the window was out of scope",
        CaptureStateReason.User => "Paused by you",
        _ => "Paused",
    };
}

/// <summary>
/// The centre pane's contents (ST-075, Spec §5 S3), derived from the session and nothing else.
///
/// The gaps in particular are derived rather than stored: they are a claim about what the session did,
/// and the only thing entitled to make it is the session's own capture-state events. A list written at
/// capture time would keep asserting a gap after the events it was built from had been edited, and would
/// assert nothing about one that appeared later.
/// </summary>
public sealed class Filmstrip
{
    private readonly Dictionary<string, bool> _included = new(StringComparer.Ordinal);
    private readonly List<FilmstripCell> _cells;

    public Filmstrip(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        // LoadSessionAsync returns redacted frames only, so everything here is safe to show (INV-1). Sorted
        // because the strip is ordered by time and the store's order is the store's business.
        var frames = session.Frames.OrderBy(frame => frame.TsMs).ToList();
        foreach (var frame in frames)
        {
            _included[frame.Id] = !frame.ExcludedByUser;
        }

        _cells = [.. frames.Select((frame, index) => (FilmstripCell)new FrameCell(frame, index + 1, _included[frame.Id]))];
        _cells.AddRange(GapsIn(session));
        _cells.Sort((a, b) => a.TsMs.CompareTo(b.TsMs));
    }

    public IReadOnlyList<FilmstripCell> Cells => _cells;

    public IEnumerable<FrameCell> Frames => _cells.OfType<FrameCell>();

    public int Total => _included.Count;

    public int IncludedCount => _included.Count(entry => entry.Value);

    /// <summary>The strip header, "7 of 14 included" (Spec §5 S3).</summary>
    public string Header => $"{IncludedCount} of {Total} included";

    public bool IsIncluded(string frameId) => _included.GetValueOrDefault(frameId);

    /// <summary>`Space`. Returns the new state, or null when the frame is not in the strip.</summary>
    public bool? Toggle(string frameId)
    {
        if (!_included.TryGetValue(frameId, out var included))
        {
            return null;
        }

        return Set(frameId, !included);
    }

    public bool? Set(string frameId, bool included)
    {
        if (!_included.ContainsKey(frameId))
        {
            return null;
        }

        _included[frameId] = included;
        Replace(frameId, cell => cell with { Included = included });
        return included;
    }

    /// <summary>
    /// What actually gets attached when the note is published. Excluded frames are absent, not marked —
    /// a publisher that had to remember to check a flag is a publisher that one day forgets.
    /// </summary>
    public IReadOnlyList<Frame> ToPublish() =>
        [.. Frames.Where(cell => cell.Included).Select(cell => cell.Frame)];

    /// <summary>The frame is gone for good. The strip renumbers, because "frame 5" means the fifth one.</summary>
    public void Remove(string frameId)
    {
        if (!_included.Remove(frameId))
        {
            return;
        }

        _cells.RemoveAll(cell => cell is FrameCell frame && frame.Frame.Id == frameId);
        Renumber();
    }

    /// <summary>Puts a deleted frame back, in its place in time, if the technician undoes within the window.</summary>
    public void Restore(Frame frame, bool included)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!_included.TryAdd(frame.Id, included))
        {
            return;
        }

        _cells.Add(new FrameCell(frame, 0, included));
        _cells.Sort((a, b) => a.TsMs.CompareTo(b.TsMs));
        Renumber();
    }

    /// <summary>The frame after a blur was applied: same id and place, new image and one more masked region.</summary>
    public void Replace(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Replace(frame.Id, cell => cell with { Frame = frame });
    }

    /// <summary>
    /// Every interval where capture was deliberately off, from the state events themselves.
    ///
    /// An interval that never closes — the session ended while suppressed — is kept and ends at the last
    /// thing the session recorded. Dropping it would hide the one case where a technician most wants to
    /// know why the end of their session has no screenshots.
    /// </summary>
    private static IEnumerable<GapCell> GapsIn(Session session)
    {
        var last = session.Events.Count > 0 ? session.Events.Max(e => e.TsMs) : 0;
        last = Math.Max(last, session.Frames.Count > 0 ? session.Frames.Max(f => f.TsMs) : 0);

        long? openedAt = null;
        CaptureStateReason? reason = null;

        foreach (var state in session.Events.OfType<CaptureStateEvent>().OrderBy(e => e.TsMs))
        {
            var off = state.State is CaptureState.Paused or CaptureState.Suppressed;
            if (off && openedAt is null)
            {
                openedAt = state.TsMs;
                reason = state.Reason;
            }
            else if (!off && openedAt is { } start)
            {
                // Zero-length intervals are dropped: a suppression that resolved in the same millisecond
                // caught nothing and drawing it would put a marker on the strip for a non-event.
                if (state.TsMs > start)
                {
                    yield return new GapCell(start, state.TsMs, reason);
                }

                openedAt = null;
                reason = null;
            }
        }

        if (openedAt is { } unclosed && last > unclosed)
        {
            yield return new GapCell(unclosed, last, reason);
        }
    }

    private void Replace(string frameId, Func<FrameCell, FrameCell> change)
    {
        var at = _cells.FindIndex(cell => cell is FrameCell frame && frame.Frame.Id == frameId);
        if (at >= 0)
        {
            _cells[at] = change((FrameCell)_cells[at]);
        }
    }

    private void Renumber()
    {
        var number = 0;
        for (var i = 0; i < _cells.Count; i++)
        {
            if (_cells[i] is FrameCell frame)
            {
                _cells[i] = frame with { Number = ++number };
            }
        }
    }
}
