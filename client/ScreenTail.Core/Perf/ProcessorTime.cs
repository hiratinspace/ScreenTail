using System.Diagnostics;

namespace ScreenTail.Core.Perf;

/// <summary>How much processor this process has used, so the throttle can tell what a session costs (ST-031).</summary>
public interface IProcessorTime
{
    /// <summary>Processor time used by every thread of this process since it started.</summary>
    TimeSpan Used { get; }

    /// <summary>The machine's logical processors, which a share of the whole machine is divided by.</summary>
    int Processors { get; }
}

/// <summary>
/// The running process's own figure: the same total Task Manager shows, read through
/// <see cref="Process.TotalProcessorTime"/>. It moves in scheduler ticks (15.6 ms on Windows), which over a
/// one-second reading on an eight-processor machine is 0.2% of the machine, well inside the budget's margin.
/// </summary>
public sealed class ThisProcessTime : IProcessorTime
{
    public TimeSpan Used
    {
        get
        {
            using var self = Process.GetCurrentProcess();
            return self.TotalProcessorTime;
        }
    }

    public int Processors { get; } = Environment.ProcessorCount;
}
