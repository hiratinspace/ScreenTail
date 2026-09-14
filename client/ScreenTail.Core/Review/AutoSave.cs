namespace ScreenTail.Core.Review;

/// <summary>What the indicator in the bottom-left of the note pane says (Spec §5 S3).</summary>
public enum SaveStatus
{
    /// <summary>Everything typed is on disk.</summary>
    Saved,

    /// <summary>A write is in flight.</summary>
    Saving,

    /// <summary>Edits are waiting for the next due moment. Shown as "Saving…" too — the distinction is
    /// ours, not the technician's, and a third word would only invite them to wonder about it.</summary>
    Pending,

    /// <summary>
    /// The last write threw. Spec §5 S3 names two labels, "Saved" and "Saving…", and neither can be shown
    /// truthfully here; a pane that looks saved while the edits are only in memory is the worst of the
    /// three. Amendment v0.4.3.
    /// </summary>
    Failed,
}

public sealed record AutoSaveOptions
{
    /// <summary>
    /// How long the typing has to stop before a save goes out. Long enough that a sentence is one write
    /// rather than forty.
    /// </summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// The longest an edit may sit unsaved, however fast the typing. ST-074 says edits persist within one
    /// second, and a plain debounce never fires at all while someone types steadily — which is precisely
    /// the moment there is most to lose.
    /// </summary>
    public TimeSpan Ceiling { get; init; } = TimeSpan.FromMilliseconds(900);

    /// <summary>How long to wait after a failed write before trying again.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Decides when the note gets written (ST-074).
///
/// It runs no timer of its own: the caller ticks it, which is the UI's dispatcher timer in the app and a
/// hand-moved clock in the tests. That keeps the whole rule — debounce, ceiling, retry, and the ordering
/// against a save that is still in flight — testable on a machine with no WPF and no Windows (ADR-0002),
/// and it means the document stays single-threaded.
///
/// <see cref="NoteDraft.Revision"/> is what it tracks, not a dirty flag. A save takes time, and a
/// technician who keeps typing during it must not be told "Saved" about text that was never written.
/// </summary>
public sealed class AutoSave : IDisposable
{
    private readonly Func<CancellationToken, Task> _write;
    private readonly Func<int> _revision;
    private readonly TimeProvider _time;
    private readonly AutoSaveOptions _options;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _stopped;
    private int _savedRevision;
    private int _inFlightRevision;
    private long? _firstUnsavedAt;
    private long _lastEditAt;
    private long? _retryAfter;

    /// <param name="write">Persists the current document. Called one at a time.</param>
    /// <param name="revision">The document's revision, read at the moment a write starts.</param>
    public AutoSave(
        Func<CancellationToken, Task> write,
        Func<int> revision,
        TimeProvider? time = null,
        AutoSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(revision);
        _write = write;
        _revision = revision;
        _time = time ?? TimeProvider.System;
        _options = options ?? new AutoSaveOptions();
        _savedRevision = revision();
    }

    public SaveStatus Status { get; private set; } = SaveStatus.Saved;

    /// <summary>What the last failed write threw, for the diagnostics panel. Never shown in the pane.</summary>
    public Exception? LastError { get; private set; }

    public int Saves { get; private set; }

    /// <summary>Call on every change to the document. Cheap, and safe to call when nothing changed.</summary>
    public void Touch()
    {
        if (_revision() == _savedRevision)
        {
            return;
        }

        _lastEditAt = _time.GetTimestamp();
        _firstUnsavedAt ??= _lastEditAt;
        if (Status is SaveStatus.Saved)
        {
            Status = SaveStatus.Pending;
        }
    }

    /// <summary>
    /// Call on a timer. Writes if a write is due; otherwise does nothing and returns immediately. A tick
    /// that arrives while a write is in flight is dropped — another one is along shortly.
    /// </summary>
    public Task TickAsync(CancellationToken ct = default) => IsDue() ? WriteAsync(wait: false, ct) : Task.CompletedTask;

    /// <summary>
    /// `Ctrl+S`, and closing the window. Writes now if anything is unsaved, ignoring the debounce and any
    /// retry delay — a technician who asked has waited long enough, and a window about to close has no
    /// later.
    ///
    /// It waits for a write already in flight rather than returning. Returning was the first version, and
    /// it dropped whatever was typed *during* that write, on the close path, silently — which is the one
    /// outcome this whole class exists to prevent.
    /// </summary>
    public Task FlushAsync(CancellationToken ct = default) => WriteAsync(wait: true, ct);

    /// <summary>
    /// The session was discarded. Nothing more may be written: the row is still there, so a later save
    /// would put a full readable note back into a session whose audit log says a human threw it away.
    /// </summary>
    public void Stop() => _stopped = true;

    public void Dispose() => _gate.Dispose();

    private async Task WriteAsync(bool wait, CancellationToken ct)
    {
        if (wait)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        else if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // Both re-read inside the gate: waiting for the in-flight write may have taken us past either.
            if (_stopped || _revision() == _savedRevision)
            {
                return;
            }

            await SaveAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsDue()
    {
        if (_firstUnsavedAt is not { } first)
        {
            return false;
        }

        if (_retryAfter is { } retry && _time.GetElapsedTime(retry) < _options.RetryAfter)
        {
            return false;
        }

        return _time.GetElapsedTime(_lastEditAt) >= _options.Quiet
            || _time.GetElapsedTime(first) >= _options.Ceiling;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        // Read before the write, not after: the document may move on while the write is in flight, and
        // this number is the one the write is about.
        _inFlightRevision = _revision();
        Status = SaveStatus.Saving;
        try
        {
            await _write(ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The edits stay unsaved and _firstUnsavedAt keeps its original value, so the ceiling is
            // measured from when the technician typed rather than from the last attempt. They have been
            // waiting longer than anyone, and the indicator says so.
            //
            // Cancellation is caught here too, and does not rethrow. It used to be excluded, which set
            // Status to Saving and then let the exception out of a method that is the only thing that
            // ever clears it - so one cancelled write left the editor reading "Saving…" and refusing every
            // subsequent tick and flush for the life of the window. A cancelled write is still a write
            // that did not happen, and that is what the indicator has to say.
            LastError = error;
            Status = SaveStatus.Failed;
            _retryAfter = _time.GetTimestamp();
            return;
        }

        Saves++;
        LastError = null;
        _retryAfter = null;
        _savedRevision = _inFlightRevision;

        if (_revision() == _savedRevision)
        {
            _firstUnsavedAt = null;
            Status = SaveStatus.Saved;
        }
        else
        {
            // Typed during the write. Those keystrokes are not on disk, so the clock for them starts now
            // rather than being inherited from edits that are.
            _firstUnsavedAt = _time.GetTimestamp();
            _lastEditAt = _firstUnsavedAt.Value;
            Status = SaveStatus.Pending;
        }
    }
}
