namespace ScreenTail.Core.Capabilities;

/// <summary>
/// A probe whose report is good for a few seconds (weakness P2-7, 2026-09-15 review).
///
/// The Windows probe installs and removes a global low-level mouse hook to find out whether hooks
/// install, and the controller ran it on every request. That cost nothing while no live screen asked;
/// the onboarding wizard's Permissions step asks on arrival and on every "Check again" (ST-083), and a
/// hook per click is felt by everything else on the machine. Five seconds is long enough that a burst
/// of requests pays once and short enough that "Check again" after a fix in Windows Settings — which
/// takes a person longer than that — sees the fix.
/// </summary>
public sealed class CachedCapabilityProbe(ICapabilityProbe inner, TimeProvider? time = null, TimeSpan? window = null) : ICapabilityProbe
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(5);

    private readonly ICapabilityProbe _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _window = window ?? DefaultWindow;
    private readonly Lock _gate = new();
    private CapabilityReport? _last;
    private DateTimeOffset _lastAt;

    public CapabilityReport Probe()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_last is not null && now - _lastAt < _window)
            {
                return _last;
            }

            _last = _inner.Probe();
            _lastAt = now;
            return _last;
        }
    }
}
