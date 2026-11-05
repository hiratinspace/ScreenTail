using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>
/// Keeps the recording clock moving between the service's snapshots (ST-072).
///
/// The service says how long a session has recorded when its state changes, and only then: a snapshot
/// carries <see cref="CaptureStateSnapshot.ElapsedMs"/> as it was when the snapshot was made. The pill, the
/// tray and the shell's status line read that number and nothing else, so through a real session they
/// showed the time the recording started at and never moved (owner's report, 2026-11-05). This advances
/// it locally: the last snapshot's elapsed time, plus how long ago that snapshot arrived.
///
/// It changes the number and nothing else. The state shown is always the service's, never the clock's
/// (ADR-0003, INV-4): a UI that decided capture had stopped because nothing arrived would be the
/// silent-capture path the whole design forbids, so silence here only ever means the clock goes on.
///
/// It ticks once a second while the service says recording, and only then. Paused, suppressed, idle and
/// "not connected" stop the timer, so a UI with nothing to count does not wake for it (ST-031). A new
/// snapshot is the new base, and within one session the clock never shows less than it already has.
/// </summary>
public sealed class RecordingClock : IDisposable
{
    /// <summary>How often the clock is redrawn while recording. The display has one-second resolution.</summary>
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly ShellState _shell;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private CaptureStateSnapshot? _observed;
    private long _receivedAt;
    private string? _session;
    private long _shownMs;
    private bool _running;
    private bool _disposed;

    /// <param name="shell">The store whose snapshots this counts from. Subscribe consumers after this.</param>
    /// <param name="time">The clock it counts with, and the one its timer runs on.</param>
    public RecordingClock(ShellState shell, TimeProvider? time = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(static self => ((RecordingClock)self!).Tick(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // Subscribed here, so a consumer made after this clock hears a change only once the clock has
        // taken the snapshot as its new base: the change is the arrival, and arrival is what it counts from.
        _shell.Changed += Observe;
        Observe(_shell.Snapshot);
    }

    /// <summary>
    /// Raised once a second while recording, on a timer thread. A consumer redraws from
    /// <see cref="Show(ShellSnapshot)"/>; it must not block, and must marshal to its own thread.
    /// </summary>
    public event Action? Ticked;

    /// <summary>Whether the timer is running: the service last said recording, and is still answering.</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>
    /// <paramref name="capture"/> with its elapsed time as it stands now. Only the elapsed time differs:
    /// every other field, the state above all, is the service's as it arrived.
    ///
    /// A snapshot other than the one this clock last took is returned as it is. That is a view redrawing
    /// from a change that has since been overtaken, and the newer change's own redraw is on its way.
    /// </summary>
    public CaptureStateSnapshot? Show(CaptureStateSnapshot? capture)
    {
        if (capture?.ElapsedMs is not { } reported)
        {
            return capture;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(capture, _observed))
            {
                return capture;
            }

            var now = reported;
            if (capture.State == CaptureStates.Recording)
            {
                now += (long)_time.GetElapsedTime(_receivedAt).TotalMilliseconds;
            }

            // Never backwards within a session. The service's number and this one can disagree by the time
            // a message spent on the pipe, and a pill that read 00:11 and then 00:10 would look broken.
            if (string.Equals(capture.SessionId, _session, StringComparison.Ordinal))
            {
                now = Math.Max(now, _shownMs);
            }

            _shownMs = now;
            return now == reported ? capture : capture with { ElapsedMs = now };
        }
    }

    /// <summary>The store's snapshot with its capture's clock as it stands now. Only a known capture is advanced.</summary>
    public ShellSnapshot Show(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.KnownCapture is not { } capture)
        {
            return snapshot;
        }

        var shown = Show(capture);
        return ReferenceEquals(shown, capture) ? snapshot : snapshot with { Capture = shown };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _running = false;
        }

        _shell.Changed -= Observe;
        _timer.Dispose();
    }

    private void Observe(ShellSnapshot snapshot)
    {
        // KnownCapture: when the service stops answering, what it last said is no longer the present, and
        // there is no clock to keep. The state shown is then "unknown, may still be recording", as before.
        var capture = snapshot.KnownCapture;
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(capture, _observed))
            {
                // The store raises on every change, a navigation as much as a new state. The same snapshot
                // again is not a new arrival, and taking it as one would set the clock back.
                return;
            }

            _observed = capture;
            _receivedAt = _time.GetTimestamp();
            if (capture is not null && !string.Equals(capture.SessionId, _session, StringComparison.Ordinal))
            {
                // Another session, or none: what the last one showed is no floor for this one.
                _session = capture.SessionId;
                _shownMs = 0;
            }

            var recording = capture is { State: CaptureStates.Recording, ElapsedMs: not null };
            if (recording)
            {
                // The first tick lands where the next whole second does, so the display turns over on the
                // second rather than up to a second late.
                var into = capture!.ElapsedMs!.Value % (long)TickEvery.TotalMilliseconds;
                var due = TimeSpan.FromMilliseconds((long)TickEvery.TotalMilliseconds - Math.Max(0, into));
                _ = _timer.Change(due, TickEvery);
            }
            else if (_running)
            {
                _ = _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            _running = recording;
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (!_running)
            {
                // A tick already on its way when the timer was stopped.
                return;
            }
        }

        Ticked?.Invoke();
    }
}
