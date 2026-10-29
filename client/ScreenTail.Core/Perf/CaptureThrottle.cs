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
    OcrDeferred,
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

    /// <summary>At <see cref="CaptureLevel.OcrDeferred"/>, how long the reader waits after a frame before the next.</summary>
    public TimeSpan OcrPause { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// ST-031 AC2: while a session records, the service watches its own processor time and, when it has been
/// over budget for ten seconds, gives up scene sampling and then OCR, in steps.
/// </summary>
public sealed class CaptureThrottle(IProcessorTime cpu, TimeProvider? time = null, CaptureThrottleOptions? options = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly CaptureThrottleOptions _options = options ?? new CaptureThrottleOptions();
    private readonly IProcessorTime _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
    private readonly TaskCompletionSource _never = new();
    private readonly CaptureLevel _level = CaptureLevel.Full;

    /// <summary>Raised on the reading loop when the level moves, so the host can log it.</summary>
    public event Action<CaptureLevelChange>? Changed;

    public CaptureThrottleOptions Options => _options;

    /// <summary>The budget on this machine, as a share of it.</summary>
    public double Budget => CpuBudget.RecordingShareOf(_cpu.Processors);

    public CaptureLevel Level => _level;

    /// <summary>The scene sampler looks at the screen on one tick in this many.</summary>
    public int SampleEveryNth => _level == CaptureLevel.Full ? 1 : 1;

    /// <summary>Whether only one frame may be read at once.</summary>
    public bool ReadsOneFrameAtATime => _level < CaptureLevel.Full;

    /// <summary>How long the reader waits after a frame before it takes the next.</summary>
    public TimeSpan OcrPause => _level < CaptureLevel.Full ? _options.OcrPause : TimeSpan.Zero;

    /// <summary>Completes the next time the level moves. Taken before the level is looked at, so no move is missed.</summary>
    public Task NextChange => _never.Task;

    /// <summary>One reading of the processor. Called by a single loop, never concurrently.</summary>
    public void Observe()
    {
        _ = Changed;
        _ = _time;
        _ = _cpu;
    }

    /// <summary>The session is over: capture goes back to full and the readings are forgotten.</summary>
    public void Reset() => _ = _level;

    /// <summary>Reads the processor while a session is under way, and puts capture back to full when it ends.</summary>
    public Task RunAsync(SessionMachine machine, Action<Exception>? onFailure, CancellationToken ct) =>
        SessionLoop.RunAsync(
            machine,
            _options.ReadEvery,
            _ =>
            {
                if (SessionLoop.IsUnderWay(machine.State))
                {
                    Observe();
                }
                else
                {
                    Reset();
                }

                return Task.CompletedTask;
            },
            onFailure,
            ct,
            _time);
}
