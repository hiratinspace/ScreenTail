using ScreenTail.Core.Sessions;

namespace ScreenTail.Core.Perf;

/// <summary>How far capture has given way to stay inside the processor budget, mildest first.</summary>
public enum CaptureLevel
{
    /// <summary>Everything as configured.</summary>
    Full,

    /// <summary>The screen is looked at every other sampling tick.</summary>
    FewerSamples,

    /// <summary>The screen is looked at every fourth tick.</summary>
    FewestSamples,

    /// <summary>As few samples, and one frame is read at a time instead of two.</summary>
    OcrOneAtATime,

    /// <summary>As few samples, one frame at a time, and a pause after each before the next is read.</summary>
    OcrClicksOnly,
}

/// <param name="From">The level before.</param>
/// <param name="To">The level now.</param>
/// <param name="Share">The last reading, as a share of the whole machine. A number, never content (INV-10).</param>
public readonly record struct CaptureLevelChange(CaptureLevel From, CaptureLevel To, double Share);

public sealed record CaptureThrottleOptions
{
    /// <summary>How often the process's processor time is read while a session is under way.</summary>
    public TimeSpan ReadEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>ST-031's criterion: this long over budget, and capture gives up one step.</summary>
    public TimeSpan OverFor { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>This long well under budget, and capture takes one step back.</summary>
    public TimeSpan UnderFor { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>"Well under": a reading below this share of the budget. Between it and the budget, the level holds.</summary>
    public double RecoverBelow { get; init; } = 0.8;

    /// <summary>At <see cref="CaptureLevel.OcrClicksOnly"/>, how long the reader waits after a frame before the next.</summary>
    public TimeSpan OcrPause { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// ST-031 AC2: while a session records, the service watches its own processor time and, when it has been
/// over budget for ten seconds, gives up scene sampling and then OCR, one step at a time.
///
/// <b>What gives, and in what order.</b> Scene sampling first, because a scene frame is the one no click
/// asked for: looking every other tick, then every fourth, costs screenshots of things a technician only
/// watched. OCR next, because it is the expensive half of redaction: one frame read at a time instead of
/// two, then a pause after each. The input hooks and speech are never touched. A click's event and its
/// screenshot are what the note is built from, and the hooks are a buffer write; speech is the technician's
/// own account of the session. Both still count towards the reading, so a busy transcription is paid for
/// out of sampling and OCR, which is the trade the ticket names.
///
/// <b>What never gives.</b> Redaction. Every frame that is taken is read, searched and painted exactly as
/// before (INV-1). Throttling only takes fewer pictures and reads them later; a frame waiting its turn waits
/// in memory, never in the store, and one that finds the queue full is refused and counted as it always was
/// (ADR-0006). The deepest pause is short enough that the four frames the queue holds are read well inside
/// finalize's twenty-second redaction grace.
///
/// <b>Hysteresis.</b> Down one step after ten seconds over the budget, and another after each further ten;
/// up one step only after thirty seconds below 80% of it. Between the two the level holds, so a load that
/// sits at the budget does not flap between levels every few seconds.
///
/// One reading a second while a session is under way and none otherwise (<see cref="SessionLoop"/>). When
/// the session ends, capture goes back to full and the readings are forgotten, so the next session is not
/// charged for what the service did in between.
/// </summary>
public sealed class CaptureThrottle
{
    private readonly IProcessorTime _cpu;
    private readonly TimeProvider _time;
    private readonly CaptureThrottleOptions _options;
    private readonly int _processors;
    private TaskCompletionSource _next = Fresh();
    private int _level;

    // The reading loop's alone: Observe and Reset run on it and nowhere else.
    private bool _started;
    private long _lastAt;
    private TimeSpan _lastUsed;
    private double _lastShare;
    private long? _overSince;
    private long? _underSince;

    public CaptureThrottle(IProcessorTime cpu, TimeProvider? time = null, CaptureThrottleOptions? options = null)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _time = time ?? TimeProvider.System;
        _options = options ?? new CaptureThrottleOptions();
        _processors = Math.Max(1, cpu.Processors);
        Budget = CpuBudget.RecordingShareOf(_processors);
    }

    /// <summary>Raised on the reading loop when the level moves, so the host can log it.</summary>
    public event Action<CaptureLevelChange>? Changed;

    public CaptureThrottleOptions Options => _options;

    /// <summary>The budget on this machine, as a share of it.</summary>
    public double Budget { get; }

    public CaptureLevel Level => (CaptureLevel)Volatile.Read(ref _level);

    /// <summary>The scene sampler looks at the screen on one tick in this many.</summary>
    public int SampleEveryNth => Level switch
    {
        CaptureLevel.Full => 1,
        CaptureLevel.FewerSamples => 2,
        _ => 4,
    };

    /// <summary>Whether the scene sampler looks at the screen at all.</summary>
    public bool LooksAtScenes => Level >= CaptureLevel.Full;

    /// <summary>Whether only one frame may be read at once.</summary>
    public bool ReadsOneFrameAtATime => Level >= CaptureLevel.OcrOneAtATime;

    /// <summary>How long the reader waits after a frame before it takes the next.</summary>
    public TimeSpan OcrPause => Level >= CaptureLevel.OcrClicksOnly ? _options.OcrPause : TimeSpan.Zero;

    /// <summary>Completes the next time the level moves. Taken before the level is looked at, so no move is missed.</summary>
    public Task NextChange => Volatile.Read(ref _next).Task;

    /// <summary>One reading of the processor. Called by a single loop, never concurrently.</summary>
    public void Observe()
    {
        var now = _time.GetTimestamp();
        var used = _cpu.Used;
        if (!_started)
        {
            // Nothing to measure against yet: this is where the next reading starts from.
            _started = true;
            (_lastAt, _lastUsed) = (now, used);
            return;
        }

        var wall = _time.GetElapsedTime(_lastAt, now);
        if (wall <= TimeSpan.Zero)
        {
            return;
        }

        var since = _lastAt;
        var share = Math.Max(0, (used - _lastUsed) / (wall * _processors));
        (_lastAt, _lastUsed, _lastShare) = (now, used, share);

        if (share > Budget)
        {
            // Over since the start of this reading, which covers the time since the last one.
            _underSince = null;
            _overSince ??= since;
            if (_time.GetElapsedTime(_overSince.Value, now) >= _options.OverFor && Level < CaptureLevel.OcrClicksOnly)
            {
                Move(Level + 1, share);
                _overSince = now;
            }
        }
        else if (share < Budget * _options.RecoverBelow)
        {
            _overSince = null;
            _underSince ??= since;
            if (_time.GetElapsedTime(_underSince.Value, now) >= _options.UnderFor && Level > CaptureLevel.Full)
            {
                Move(Level - 1, share);
                _underSince = now;
            }
        }
        else
        {
            // Inside the band: neither over nor well under, so neither count goes on.
            _overSince = null;
            _underSince = null;
        }
    }

    /// <summary>The session is over: capture goes back to full and the readings are forgotten.</summary>
    public void Reset()
    {
        _started = false;
        _overSince = null;
        _underSince = null;
        if (Level != CaptureLevel.Full)
        {
            Move(CaptureLevel.Full, _lastShare);
        }
    }

    /// <summary>Reads the processor while a session is under way, and puts capture back to full when it ends.</summary>
    public Task RunAsync(SessionMachine machine, Action<Exception>? onFailure, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return SessionLoop.RunAsync(
            machine,
            _options.ReadEvery,
            _ =>
            {
                Turn(machine);
                return Task.CompletedTask;
            },
            onFailure,
            ct,
            _time);
    }

    /// <summary>One turn of the reading loop.</summary>
    internal void Turn(SessionMachine machine)
    {
        if (SessionLoop.IsUnderWay(machine.State))
        {
            Observe();
        }
        else
        {
            Reset();
        }
    }

    private void Move(CaptureLevel to, double share)
    {
        var from = Level;
        Volatile.Write(ref _level, (int)to);

        // After the level, so a waiter that took the old signal and then read the level cannot miss the move.
        Interlocked.Exchange(ref _next, Fresh()).TrySetResult();
        Changed?.Invoke(new CaptureLevelChange(from, to, share));
    }

    private static TaskCompletionSource Fresh() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
