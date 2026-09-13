namespace ScreenTail.Core.Capture;

/// <summary>
/// Decides which clicks are worth a screenshot (ST-025).
///
/// A technician double-clicks, drags, clicks through a menu: five clicks in half a second are one action and
/// deserve one frame. The first click of a burst is the one captured rather than the last, because the
/// frame is taken to show what was on screen when they acted — waiting for the burst to end would photograph
/// the consequence instead of the cause.
/// </summary>
public sealed class ClickDebouncer(TimeProvider? time = null, TimeSpan? window = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _window = window ?? TimeSpan.FromMilliseconds(400);
    private long _lastCapture;
    private long _suppressed;

    /// <summary>Clicks that shared a frame with an earlier one. Shown in diagnostics, never lost silently.</summary>
    public long Suppressed => Interlocked.Read(ref _suppressed);

    public TimeSpan Window => _window;

    /// <summary>Whether this click should produce a frame.</summary>
    public bool ShouldCapture()
    {
        var now = _time.GetTimestamp();
        if (_lastCapture != 0 && _time.GetElapsedTime(_lastCapture, now) < _window)
        {
            Interlocked.Increment(ref _suppressed);
            return false;
        }

        _lastCapture = now;
        return true;
    }

    /// <summary>A new session starts with no history, so its first click is always captured.</summary>
    public void Reset()
    {
        _lastCapture = 0;
        Interlocked.Exchange(ref _suppressed, 0);
    }
}
