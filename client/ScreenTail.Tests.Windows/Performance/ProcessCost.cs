using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ScreenTail.Tests.Windows.Performance;

/// <summary>
/// What this process had spent at one moment (ST-031): processor time, how many times its threads were
/// switched in, and the memory it holds.
/// </summary>
/// <param name="Cpu">Processor time as Windows reports it, charged a clock tick (15.625 ms) at a time to
/// whichever thread was running when the tick came. Fine for a busy process and a coin toss for an idle
/// one, so it is the cross-check, not the figure.</param>
/// <param name="Cycles">Processor cycles this process has run for, counted exactly, which is what an idle
/// figure is taken from (<see cref="CycleCounter"/>).</param>
/// <param name="Switches">Context switches so far, by thread id. A switch in is a wakeup, which is the
/// number an idle process should keep near zero: each one is a core leaving a low-power state to run a
/// timer callback that, at idle, usually decides there was nothing to do.</param>
internal sealed record ProcessSample(
    long Timestamp,
    TimeSpan Cpu,
    ulong Cycles,
    IReadOnlyDictionary<long, uint> Switches,
    long PrivateBytes,
    long WorkingSet,
    int Threads,
    int Handles);

/// <summary>
/// Reads this process's own cost, the way the laptop's ST-031 measurements need it.
///
/// <b>In-process, on purpose.</b> The real service and the real UI open the technician's own store,
/// key, token and pipe, which on the laptop are the demo's, so CI does not start them. These tests compose
/// the same classes the service and the shell compose and measure what they add to this process, net of a
/// baseline taken just before. That is the cost the code is responsible for. The whole process (the
/// runtime, the loaded assemblies, SQLCipher, the OCR engine) is measured on the real processes with
/// <c>scripts/windows/measure-idle.ps1</c>; see docs/dev/performance.md.
/// </summary>
internal static class ProcessCost
{
    public static ProcessSample Sample()
    {
        using var self = Process.GetCurrentProcess();
        return new ProcessSample(
            Stopwatch.GetTimestamp(),
            self.TotalProcessorTime,
            CycleCounter.OfThisProcess(),
            ThreadSwitches.Read(self.Id),
            self.PrivateMemorySize64,
            self.WorkingSet64,
            self.Threads.Count,
            self.HandleCount);
    }

    /// <summary>
    /// Samples at the start and after each slice, so the spread across slices can be reported alongside
    /// the total. Rates come from the time that actually passed between samples, never from the slice
    /// length asked for, so a late timer makes a slice longer rather than a number wrong.
    /// </summary>
    public static async Task<CostWindow> MeasureAsync(TimeSpan slice, int slices, CancellationToken ct)
    {
        // Calibrated before the first sample: the calibration spins, and a spin inside a window would be
        // three tenths of a second of the test's work counted as the service's.
        _ = CycleCounter.PerSecond;
        var samples = new List<ProcessSample>(slices + 1) { Sample() };
        for (var i = 0; i < slices; i++)
        {
            await Task.Delay(slice, ct);
            samples.Add(Sample());
        }

        return new CostWindow(samples);
    }

    /// <summary>Collects what can be collected, so a memory figure is not a figure about garbage.</summary>
    public static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>A run of samples, and the rates between them.</summary>
internal sealed class CostWindow
{
    private readonly IReadOnlyList<ProcessSample> _samples;

    public CostWindow(IReadOnlyList<ProcessSample> samples)
    {
        if (samples.Count < 2)
        {
            throw new ArgumentException("A window needs at least two samples.", nameof(samples));
        }

        _samples = samples;
    }

    public ProcessSample First => _samples[0];

    /// <summary>
    /// The same window without some threads' switches: the noise a test raises on purpose, on threads of
    /// its own, so the wakeup figures are the rest of the process's. Processor time is left as it was.
    /// </summary>
    public CostWindow Excluding(IReadOnlyCollection<long> threads) =>
        new([.. _samples.Select(sample => sample with
        {
            Switches = sample.Switches.Where(thread => !threads.Contains(thread.Key)).ToDictionary(thread => thread.Key, thread => thread.Value),
        })]);

    public ProcessSample Last => _samples[^1];

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(First.Timestamp, Last.Timestamp);

    /// <summary>Processor time over the whole window as a percentage of one core, from cycles.</summary>
    public double CpuPercentOfOneCore => Percent(First, Last);

    /// <summary>The same, as a share of every logical processor this machine has.</summary>
    public double CpuPercentOfMachine => CpuPercentOfOneCore / Environment.ProcessorCount;

    /// <summary>Threads switched in per second over the whole window.</summary>
    public double WakeupsPerSecond => Wakeups(First, Last);

    /// <summary>Each slice's processor time as a percentage of one core, from cycles.</summary>
    public IReadOnlyList<double> CpuBySlice => Slices(Percent);

    /// <summary>
    /// Each slice's processor time as Windows' clock-tick accounting has it, as a percentage of one core.
    ///
    /// Reported beside the cycle figure because it is what Task Manager shows, and because the second
    /// laptop run showed how far off it is at idle: a five-second slice can only read 0, 0.31, 0.62 or
    /// 0.94 per cent and so on, and the idle service's median came out at 0.94, three ticks, for about
    /// twenty-five wakeups a second.
    /// </summary>
    public IReadOnlyList<double> TickCpuBySlice => Slices(TickPercent);

    /// <summary>Each slice's wakeups per second.</summary>
    public IReadOnlyList<double> WakeupsBySlice => Slices(Wakeups);

    /// <summary>
    /// The middle slice's processor time, as a percentage of one core.
    ///
    /// What a budget is held against. The first laptop run showed why: the test host's own baseline,
    /// taken a moment after another test had finished, came in at six per cent of a core for one slice
    /// and made a total-over-total difference negative. A median ignores a slice or two of somebody
    /// else's work at either end; a total does not.
    /// </summary>
    public double MedianCpu => Median(CpuBySlice);

    /// <summary>The middle slice's wakeups per second.</summary>
    public double MedianWakeups => Median(WakeupsBySlice);

    private static double Median(IReadOnlyList<double> values) => values.Order().ElementAt(values.Count / 2);

    /// <summary>
    /// "12 threads woke; the busiest 41.2 (ScreenTail foreground watcher), 20.1 (.NET TP Worker), 9.9 (.NET
    /// Timer)/s": whether the wakeups are one loop or many, and whose. The name is the thread's own
    /// description, which .NET sets from <see cref="Thread.Name"/> and for its pool, timer and compiler
    /// threads; a thread with none is named by the module it started in. Never anything a window shows.
    ///
    /// The laptop's idle failures (2026-11-01) said "the busiest 29.4, 6.2, 5.2/s" and nothing about whose
    /// the 29.4 was; this says.
    /// </summary>
    public string ThreadsWoken(int busiest = 3)
    {
        var elapsed = Elapsed.TotalSeconds;
        var rates = Last.Switches
            .Select(thread => (Thread: thread.Key, Rate: (First.Switches.TryGetValue(thread.Key, out var before) ? unchecked(thread.Value - before) : thread.Value) / elapsed))
            .Where(thread => thread.Rate > 0)
            .OrderByDescending(thread => thread.Rate)
            .ToList();
        var culture = CultureInfo.InvariantCulture;
        return string.Create(
            culture,
            $"{rates.Count} threads woke; the busiest {string.Join(", ", rates.Take(busiest).Select(thread => string.Create(culture, $"{thread.Rate:F1} ({ThreadNames.Of(thread.Thread)})")))}/s");
    }

    /// <summary>One thread's wakeups a second over the whole window; zero for a thread not seen at the end.</summary>
    public double RateOf(long thread)
    {
        var elapsed = Elapsed.TotalSeconds;
        if (elapsed <= 0 || !Last.Switches.TryGetValue(thread, out var after))
        {
            return 0;
        }

        return unchecked(after - First.Switches.GetValueOrDefault(thread)) / elapsed;
    }

    private List<double> Slices(Func<ProcessSample, ProcessSample, double> rate)
    {
        var rates = new List<double>(_samples.Count - 1);
        for (var i = 1; i < _samples.Count; i++)
        {
            rates.Add(rate(_samples[i - 1], _samples[i]));
        }

        return rates;
    }

    private static double Percent(ProcessSample from, ProcessSample to)
    {
        var elapsed = Stopwatch.GetElapsedTime(from.Timestamp, to.Timestamp);
        return elapsed <= TimeSpan.Zero ? 0 : (to.Cycles - from.Cycles) / CycleCounter.PerSecond / elapsed.TotalSeconds * 100;
    }

    private static double TickPercent(ProcessSample from, ProcessSample to)
    {
        var elapsed = Stopwatch.GetElapsedTime(from.Timestamp, to.Timestamp);
        return elapsed <= TimeSpan.Zero ? 0 : (to.Cpu - from.Cpu).TotalMilliseconds / elapsed.TotalMilliseconds * 100;
    }

    /// <summary>
    /// Switches between two samples, per second.
    ///
    /// Thread by thread: a thread that started in between counts from zero, and one that ended in
    /// between drops out, which undercounts by whatever it did before it went. The thread pool retires
    /// idle threads after about twenty seconds, so a total taken over the whole process would go down.
    /// </summary>
    private static double Wakeups(ProcessSample from, ProcessSample to)
    {
        long switches = 0;
        foreach (var (thread, after) in to.Switches)
        {
            switches += from.Switches.TryGetValue(thread, out var before) ? unchecked(after - before) : after;
        }

        var elapsed = Stopwatch.GetElapsedTime(from.Timestamp, to.Timestamp);
        return elapsed <= TimeSpan.Zero ? 0 : switches / elapsed.TotalSeconds;
    }

    /// <summary>"median 0.04 (0.00 to 0.31)": the middle slice and the range, so one bad slice is visible.</summary>
    public static string Spread(IReadOnlyList<double> values, string format = "F2")
    {
        var sorted = values.Order().ToList();
        var culture = CultureInfo.InvariantCulture;
        return string.Create(
            culture,
            $"median {sorted[sorted.Count / 2].ToString(format, culture)} ({sorted[0].ToString(format, culture)} to {sorted[^1].ToString(format, culture)})");
    }
}

/// <summary>
/// The processor cycles this process has run for, and how many of them make a second.
///
/// Windows keeps an exact count of the cycles each thread runs for, beside the clock-tick charge that
/// <see cref="Process.TotalProcessorTime"/> reports. On the processors ScreenTail supports the counter
/// runs at a constant rate whatever the clock speed, so cycles convert to time once the rate is known,
/// and the rate is measured rather than assumed: a thread spins for a tenth of a second three times and
/// the fastest rate seen is kept, because a spin the scheduler interrupted counts fewer cycles, never
/// more.
/// </summary>
internal static class CycleCounter
{
    private static readonly Lazy<double> Rate = new(Calibrate);

    /// <summary>Cycles in a second of running.</summary>
    public static double PerSecond => Rate.Value;

    public static ulong OfThisProcess() =>
        QueryProcessCycleTime(GetCurrentProcess(), out var cycles) ? cycles : throw new Win32Exception(Marshal.GetLastWin32Error());

    private static double Calibrate()
    {
        var fastest = 0.0;
        var thread = GetCurrentThread();
        for (var i = 0; i < 3; i++)
        {
            _ = QueryThreadCycleTime(thread, out var before);
            var start = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromMilliseconds(100))
            {
            }

            _ = QueryThreadCycleTime(thread, out var after);
            fastest = Math.Max(fastest, (after - before) / Stopwatch.GetElapsedTime(start).TotalSeconds);
        }

        return fastest > 0 ? fastest : throw new InvalidOperationException("The cycle counter did not advance during a spin.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();
}

/// <summary>
/// Per-thread context-switch counts for one process, from <c>NtQuerySystemInformation</c>.
///
/// Windows keeps the count in the kernel's thread record and publishes it only through the system
/// process list. The layout is the 64-bit <c>SYSTEM_PROCESS_INFORMATION</c> with its
/// <c>SYSTEM_THREAD_INFORMATION</c> array, and it is checked rather than trusted: every thread record
/// found under this process must name this process as its owner, or the read throws instead of handing
/// back numbers from the wrong offsets.
/// </summary>
internal static class ThreadSwitches
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // SYSTEM_PROCESS_INFORMATION (x64 and ARM64)
    private const int NextEntryOffset = 0x00;
    private const int NumberOfThreads = 0x04;
    private const int UniqueProcessId = 0x50;
    private const int ThreadsArray = 0x100;

    // SYSTEM_THREAD_INFORMATION (x64 and ARM64)
    private const int ThreadRecordSize = 0x50;
    private const int ClientIdProcess = 0x28;
    private const int ClientIdThread = 0x30;
    private const int ContextSwitches = 0x40;

    public static IReadOnlyDictionary<long, uint> Read(int processId)
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("The thread records are read at their 64-bit offsets.");
        }

        var size = 1 << 20;
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, buffer, size, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    size = Math.Max(size * 2, needed + (64 * 1024));
                    continue;
                }

                if (status != 0)
                {
                    throw new InvalidOperationException($"NtQuerySystemInformation returned 0x{status:X8}.");
                }

                return Find(buffer, processId);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static Dictionary<long, uint> Find(IntPtr buffer, int processId)
    {
        var entry = buffer;
        while (true)
        {
            if (Marshal.ReadIntPtr(entry, UniqueProcessId).ToInt64() == processId)
            {
                var count = Marshal.ReadInt32(entry, NumberOfThreads);
                var switches = new Dictionary<long, uint>(count);
                for (var i = 0; i < count; i++)
                {
                    var thread = entry + ThreadsArray + (i * ThreadRecordSize);
                    var owner = Marshal.ReadIntPtr(thread, ClientIdProcess).ToInt64();
                    if (owner != processId)
                    {
                        throw new InvalidOperationException(
                            $"Thread record {i} names process {owner}, not {processId}: the SYSTEM_THREAD_INFORMATION layout is not the one this reader assumes.");
                    }

                    switches[Marshal.ReadIntPtr(thread, ClientIdThread).ToInt64()] = unchecked((uint)Marshal.ReadInt32(thread, ContextSwitches));
                }

                return switches;
            }

            var next = Marshal.ReadInt32(entry, NextEntryOffset);
            if (next == 0)
            {
                throw new InvalidOperationException($"Process {processId} is not in the system process list.");
            }

            entry += next;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
}

/// <summary>
/// What a thread of this process calls itself, for a report: its description (<c>GetThreadDescription</c>,
/// which .NET fills from <see cref="Thread.Name"/> and for its own pool, timer and compiler threads), or the
/// module its start address is in. "?" for a thread that has gone.
/// </summary>
internal static class ThreadNames
{
    private const uint ThreadQueryLimitedInformation = 0x0800;
    private const uint ThreadQueryInformation = 0x0040;
    private const int ThreadQuerySetWin32StartAddress = 9;
    private const uint FromAddress = 0x4;
    private const uint UnchangedRefCount = 0x2;

    public static string Of(long thread)
    {
        var handle = OpenThread(ThreadQueryLimitedInformation | ThreadQueryInformation, false, (uint)thread);
        if (handle == IntPtr.Zero)
        {
            return "?";
        }

        try
        {
            if (GetThreadDescription(handle, out var text) >= 0 && text != IntPtr.Zero)
            {
                var name = Marshal.PtrToStringUni(text);
                _ = LocalFree(text);
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            if (NtQueryInformationThread(handle, ThreadQuerySetWin32StartAddress, out var start, IntPtr.Size, out _) == 0
                && start != IntPtr.Zero
                && GetModuleHandleEx(FromAddress | UnchangedRefCount, start, out var module)
                && module != IntPtr.Zero)
            {
                var buffer = new char[260];
                var length = GetModuleFileName(module, buffer, buffer.Length);
                if (length > 0)
                {
                    return Path.GetFileName(new string(buffer, 0, length));
                }
            }

            return "unnamed";
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint threadId);

    [DllImport("kernel32.dll")]
    private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr thread, int infoClass, out IntPtr info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetModuleHandleEx(uint flags, IntPtr address, out IntPtr module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetModuleFileName(IntPtr module, [Out] char[] buffer, int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
