namespace ScreenTail.Spike.Core;

/// <summary>
/// Detects a low-level hook that Windows silently removed (callback exceeded LowLevelHooksTimeout).
/// Compares the system's last-input tick (GetLastInputInfo) with the last tick our hook saw.
/// Both are GetTickCount-domain values, so the arithmetic tolerates the 49.7-day wrap.
/// </summary>
/// <remarks>
/// False positives: input to an elevated window or the secure desktop updates GetLastInputInfo
/// but is not delivered to a medium-integrity hook. The spike run types in a normal window.
/// </remarks>
public sealed class HookHealthMonitor
{
    private readonly uint _toleranceMs;
    private readonly int _strikesToFlag;
    private int _consecutiveStrikes;

    public HookHealthMonitor(uint toleranceMs = 2000, int strikesToFlag = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(strikesToFlag, 1);
        _toleranceMs = toleranceMs;
        _strikesToFlag = strikesToFlag;
    }

    public bool SuspectedUnhooked { get; private set; }

    public int TotalStrikes { get; private set; }

    public void Check(uint lastSystemInputTick, uint lastHookTick)
    {
        var lead = unchecked((int)(lastSystemInputTick - lastHookTick));
        if (lead > _toleranceMs)
        {
            TotalStrikes++;
            if (++_consecutiveStrikes >= _strikesToFlag)
            {
                SuspectedUnhooked = true;
            }
        }
        else
        {
            _consecutiveStrikes = 0;
        }
    }
}
