namespace ScreenTail.Core.Capture;

/// <summary>Why a sample did not become a screenshot. Counted for the diagnostics panel (INV-10).</summary>
public enum SceneDecision
{
    /// <summary>The first sample of a session: it becomes the baseline, and there is nothing to compare it to.</summary>
    Baseline,

    /// <summary>The screen looks like the last frame kept, so there is nothing new to show.</summary>
    Unchanged,

    /// <summary>The screen changed, but a frame was kept a moment ago and this is the same event still settling.</summary>
    TooSoon,

    /// <summary>The screen changed, but this session has had its share of scene frames for now.</summary>
    OverBudget,

    Keep,
}

public sealed record SceneSamplerOptions
{
    /// <summary>How often the screen is looked at. The ticket says 1 fps.</summary>
    public TimeSpan SampleEvery { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many of the 256 bits must differ before this counts as a new thing to show.
    ///
    /// Measured, not guessed: on a synthetic 1080p desktop a caret scores 0, a taskbar clock 0 and heavy
    /// pixel noise 3, while dialogs from 300×160 upwards score 19 to 33. Ten sits in that gap with room on
    /// both sides, and leans towards noticing — an extra frame costs a little disk and is capped by
    /// <see cref="Budget"/>, while a missed dialog is the screenshot the ticket note needed.
    /// </summary>
    public int Threshold { get; init; } = 10;

    /// <summary>
    /// The quiet period after a frame is kept. A window opening redraws over several samples, and all of
    /// them are the same event; without this each one would be a separate screenshot of the same dialog
    /// half-drawn. ST-026 wants a frame within 2 s of a dialog appearing, so this has to stay well inside it.
    /// </summary>
    public TimeSpan MinimumGap { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>ST-026's cap: no more than <see cref="Budget"/> scene frames in any <see cref="BudgetWindow"/>.</summary>
    public int Budget { get; init; } = 60;

    public TimeSpan BudgetWindow { get; init; } = TimeSpan.FromMinutes(20);
}

/// <param name="Kept">Samples that became screenshots.</param>
/// <param name="Unchanged">Samples of a screen that had not changed. The common case, and the point.</param>
public sealed record SceneCounts(long Seen, long Kept, long Unchanged, long TooSoon, long OverBudget);

/// <summary>
/// Decides which of the once-a-second samples are worth a screenshot (ST-026).
///
/// ST-025 photographs clicks, which misses everything a technician reads rather than does: an error dialog
/// appearing while they watch, a progress bar finishing, a service finally starting. Those are often the
/// screenshot the ticket note actually needs, and no click marks them.
///
/// The whole difficulty is the other direction. A screen is never perfectly still — a caret blinks, a clock
/// ticks, an antivirus icon animates — and a sampler that treats any difference as news would fill a
/// twenty-minute session with a thousand pictures of the same desktop, blow ST-005's disk budget, and bury
/// the useful frames. So three things have to be true before a sample is kept: it looks different from the
/// last frame kept, the last one was not a moment ago, and this session has not already had its share.
///
/// The budget is an exact count over a sliding window rather than a token bucket, because "no more than 60
/// in any twenty minutes" is what the criterion says and a bucket only approximates it. Sixty timestamps is
/// nothing to keep.
/// </summary>
public sealed class SceneSampler(TimeProvider? time = null, SceneSamplerOptions? options = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SceneSamplerOptions _options = options ?? new SceneSamplerOptions();
    private readonly Queue<DateTimeOffset> _kept = new();
    private SceneHash? _baseline;
    private DateTimeOffset _lastKept;
    private long _seen;
    private long _keptCount;
    private long _unchanged;
    private long _tooSoon;
    private long _overBudget;

    public SceneSamplerOptions Options => _options;

    public SceneCounts Counts => new(_seen, _keptCount, _unchanged, _tooSoon, _overBudget);

    /// <summary>
    /// Whether this sample is worth storing. The baseline only moves when a frame is kept, so a screen that
    /// drifts slowly still trips the threshold eventually — the question is "has it changed since the last
    /// picture we have", not "since a second ago".
    /// </summary>
    public SceneDecision Offer(SceneHash hash)
    {
        var now = _time.GetUtcNow();
        _seen++;

        if (_baseline is not { } baseline)
        {
            // Nothing to compare against yet. Deliberately not kept: a session that opens on a still screen
            // and stays still should produce no scene frames at all, and clicks already cover the start.
            _baseline = hash;
            _lastKept = now;
            return SceneDecision.Baseline;
        }

        if (baseline.DistanceTo(hash) < _options.Threshold)
        {
            _unchanged++;
            return SceneDecision.Unchanged;
        }

        if (now - _lastKept < _options.MinimumGap)
        {
            _tooSoon++;
            return SceneDecision.TooSoon;
        }

        Forget(now);
        if (_kept.Count >= _options.Budget)
        {
            // Not an error and not silent: Review says how many scene frames were dropped, the same way it
            // accounts for frames that could not be redacted.
            _overBudget++;
            return SceneDecision.OverBudget;
        }

        _baseline = hash;
        _lastKept = now;
        _kept.Enqueue(now);
        _keptCount++;
        return SceneDecision.Keep;
    }

    /// <summary>A new session starts with no history, so its first sample is a baseline again.</summary>
    public void Reset()
    {
        _baseline = null;
        _kept.Clear();
        _lastKept = default;
        _seen = _keptCount = _unchanged = _tooSoon = _overBudget = 0;
    }

    private void Forget(DateTimeOffset now)
    {
        while (_kept.Count > 0 && now - _kept.Peek() >= _options.BudgetWindow)
        {
            _ = _kept.Dequeue();
        }
    }
}
