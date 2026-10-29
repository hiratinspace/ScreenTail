namespace ScreenTail.Core.Perf;

/// <summary>
/// ST-031's processor budget for a recording session, in one place.
///
/// The ticket says 15% of a four-core machine, which is an amount of processor (60% of one core) as much as
/// it is a share. A machine with more cores is held to the same amount, which is what the laptop check in
/// <c>ServiceCostTests</c> enforces; one with fewer is held to 15% of itself, so a dual-core laptop is never
/// allowed a third of its processor because the reference machine had four.
/// </summary>
public static class CpuBudget
{
    /// <summary>The share of the reference machine a recording session may use.</summary>
    public const double Recording = 0.15;

    /// <summary>The reference machine's logical processors.</summary>
    public const int ReferenceProcessors = 4;

    /// <summary>The recording budget as a share of a machine with this many logical processors.</summary>
    public static double RecordingShareOf(int processors)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processors, 1);
        return Recording;
    }
}
