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
/// It ticks about once a second while the service says recording, and only then. Paused, suppressed,
/// idle and "not connected" stop the timer, so a UI with nothing to count does not wake for it (ST-031).
/// A new snapshot is the new base, and within one session the clock never shows less than it already has.
/// </summary>
public sealed class RecordingClock : IDisposable
{
    /// <summary>The display's resolution: one second.</summary>
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far past each whole second a tick is aimed. Windows runs thread-pool timers on its interrupt
    /// clock, about 15.6 ms a step, so a timer aimed exactly at the boundary lands a few milliseconds
    /// either side of it: early shows the old second again, and a fixed period drifting through the
    /// boundary shows one second twice and skips the next (2026-11-05 review). Fifty milliseconds is
    /// more than three steps clear, and too little for anyone to see.
    /// </summary>
    public static readonly TimeSpan PastTheSecond = TimeSpan.FromMilliseconds(50);

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
    private long _handlerFailures;

    /// <param name="shell">The store whose snapshots this counts from. Subscribe consumers after this.</param>
    /// <param name="time">The clock it counts with, and the one its timer runs on.</param>
    public RecordingClock(ShellState shell, TimeProvider? time = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(static self => ((RecordingClock)self!).Tick(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // Subscribed here, so a consumer made after this clock hears a change only once the clock has
        // taken the snapshot as its new base: the change is the arrival, and arrival is what it counts from.
        _shell.Changed += OnChanged;
        Observe();
    }

    /// <summary>
    /// Raised about once a second while recording, on a timer thread. A consumer redraws from
    /// <see cref="Show(ShellSnapshot)"/>; it must not block, and must marshal to its own thread. Each
    /// handler is called on its own, so one that throws neither stops the others nor the clock.
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

    /// <summary>How many times a <see cref="Ticked"/> handler threw. Counts only, never the message (INV-10).</summary>
    public long HandlerFailures => Interlocked.Read(ref _handlerFailures);

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

            var now = Now(capture, reported);
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

        _shell.Changed -= OnChanged;
        _timer.Dispose();
    }

    /// <summary>
    /// The store's change event is a nudge, not the news. <see cref="ShellState"/> raises it outside its
    /// lock, so two changes on two threads can arrive here in the other order, and the handshake's own
    /// change can land after a later one (2026-11-05 review). Taking the argument would then keep a
    /// recording clock running over a store that says the service has gone. The store itself is read
    /// instead: whatever order the nudges come in, the last one leaves the clock on what the store holds.
    /// </summary>
    private void OnChanged(ShellSnapshot _) => Observe();

    private void Observe()
    {
        // KnownCapture: when the service stops answering, what it last said is no longer the present, and
        // there is no clock to keep. The state shown is then "unknown, may still be recording", as before.
        lock (_gate)
        {
            var capture = _shell.Snapshot.KnownCapture;
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

            _running = capture is { State: CaptureStates.Recording, ElapsedMs: not null };
            Arm();
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

        if (Ticked is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Action>())
            {
                try
                {
                    handler();
                }
#pragma warning disable CA1031 // The point: a handler's fault must not take the other views, or the clock, with it.
                catch (Exception)
#pragma warning restore CA1031
                {
                    _ = Interlocked.Increment(ref _handlerFailures);
                }
            }
        }

        lock (_gate)
        {
            Arm();
        }
    }

    /// <summary>
    /// One shot, aimed just past the next whole second of the time as it now stands, and aimed again from
    /// there after every tick. Re-aiming each time keeps a tick in the right second however early or late
    /// the last one came; a fixed period carries every error forward into the next. Under the gate.
    /// </summary>
    private void Arm()
    {
        if (!_running || _disposed || _observed?.ElapsedMs is not { } reported)
        {
            _ = _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var second = (long)TickEvery.TotalMilliseconds;
        var now = Now(_observed, reported);
        var due = TimeSpan.FromMilliseconds(second - (now % second)) + PastTheSecond;
        _ = _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The time to show for the snapshot this clock holds, never less than was shown. Under the gate.</summary>
    private long Now(CaptureStateSnapshot capture, long reported)
    {
        var now = reported;
        if (capture.State == CaptureStates.Recording)
        {
            now += (long)_time.GetElapsedTime(_receivedAt).TotalMilliseconds;
        }

        // Never backwards within a session. The service's number and this one can disagree by the time a
        // message spent on the pipe, and a pill that read 00:11 and then 00:10 would look broken.
        if (string.Equals(capture.SessionId, _session, StringComparison.Ordinal))
        {
            now = Math.Max(now, _shownMs);
        }

        return Math.Max(0, now);
    }
}
