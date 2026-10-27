using System.Globalization;
using System.Runtime.Versioning;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.Tests.Windows.Shell;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Performance;

/// <summary>
/// ST-031's idle budget for the UI: the real shell (tray icon, the pill, the connection and its indicator
/// reports) connected to a service with no session, measured the way <see cref="ServiceCostTests"/>
/// measures the service, net of the test host.
///
/// The pill says it is on screen every two seconds, on the UI thread, whether or not a session runs: the
/// service asks for it before starting a session (INV-4), and a report from any other thread would go on
/// while the UI was frozen. So the UI is never completely quiet at idle, and the count of reports is in
/// the line this writes.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellCostTests
{
    private const double IdleBudgetPercentOfOneCore = 1.0;

    private static bool Counts => Hardware.PerformanceCounts;

    private static TimeSpan Slice => Counts ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(1);

    private static TimeSpan SettleFor => Counts ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2);

    [Fact]
    public async Task WithNoSessionTheShellIsAlmostSilent()
    {
        Hardware.RequireDesktop();
        var ct = TestContext.Current.CancellationToken;

        ProcessCost.Settle();
        var host = await ProcessCost.MeasureAsync(Slice, 3, ct);

        CostWindow? idle = null;
        var connected = false;
        var reports = 0;
        var file = Path.Combine(Path.GetTempPath(), $"screentail-shell-{Guid.NewGuid():N}.json");
        Assert.True(new ShellPreferencesStore(file).Save(new ShellPreferences { OnboardedAt = DateTimeOffset.UnixEpoch }), "the test could not write its preferences file");
        try
        {
            StaThread.Run(
                async () =>
                {
                    var service = new IdleService();
                    var shell = new LiveShell((_, _) => Task.FromResult<ICaptureChannel>(service), new ShellPreferencesStore(file));
                    try
                    {
                        await shell.StartAsync();
                        connected = await StaThread.UntilAsync(
                            () => shell.State.Snapshot.Connection == ServiceConnection.Connected && shell.Pill is not null,
                            TimeSpan.FromSeconds(10));
                        if (!connected)
                        {
                            return;
                        }

                        await Task.Delay(SettleFor);
                        ProcessCost.Settle();
                        var before = service.IndicatorReports;
                        idle = await ProcessCost.MeasureAsync(Slice, 6, CancellationToken.None);
                        reports = service.IndicatorReports - before;
                    }
                    finally
                    {
                        await shell.DisposeAsync();
                    }
                },
                within: TimeSpan.FromMinutes(2));
        }
        finally
        {
            File.Delete(file);
        }

        Assert.True(connected, "the shell never connected to the idle service or never built the pill");
        Assert.NotNull(idle);
        var cpu = idle.MedianCpu - host.MedianCpu;
        var wakeups = idle.MedianWakeups - host.MedianWakeups;
        Measurements.Record(string.Create(
            CultureInfo.InvariantCulture,
            $"Shell at idle (tray, pill, connection), {idle.Elapsed.TotalSeconds:F0} s in {Slice.TotalSeconds:F0} s slices after {SettleFor.TotalSeconds:F0} s to settle: "
            + $"**{cpu:F3}%** of one core net of the test host, median slice against median slice ({cpu / Environment.ProcessorCount:F3}% of {Environment.ProcessorCount} logical processors; "
            + $"shell {CostWindow.Spread(idle.CpuBySlice, "F3")}, host alone {CostWindow.Spread(host.CpuBySlice, "F3")}; by clock ticks, as Task Manager counts, shell {CostWindow.Spread(idle.TickCpuBySlice, "F3")}), "
            + $"**{wakeups:F1} wakeups/s** net (shell {CostWindow.Spread(idle.WakeupsBySlice, "F1")}, host alone {CostWindow.Spread(host.WakeupsBySlice, "F1")}; {idle.ThreadsWoken()}), "
            + $"{reports} indicator reports to the service ({reports / idle.Elapsed.TotalSeconds:F2}/s), "
            + $"private memory {ProcessCost.Megabytes(idle.Last.PrivateBytes - host.Last.PrivateBytes)} over the host "
            + $"(budget {IdleBudgetPercentOfOneCore:F0}% of one core, enforced: {Counts}; wakeups and memory reported, not enforced)"));

        Assert.True(
            !Counts || cpu < IdleBudgetPercentOfOneCore,
            $"the idle shell used {cpu:F3}% of one core net of the host, budget {IdleBudgetPercentOfOneCore:F0}%");
    }

    /// <summary>A service with no session that answers every command and counts the pill's reports.</summary>
    private sealed class IdleService : ICaptureChannel
    {
        private int _reports;
        private volatile bool _disposed;

        public CaptureStateSnapshot State { get; } = new() { State = CaptureStates.Idle };

        public bool IsConnected => !_disposed;

        public int IndicatorReports => Volatile.Read(ref _reports);

        public event Action<CaptureStateSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public Task<CommandResult> SendAsync(Func<int, IpcCommand> build, CancellationToken ct = default)
        {
            if (build(1) is IndicatorShowingCommand)
            {
                _ = Interlocked.Increment(ref _reports);
            }

            return Task.FromResult(new CommandResult { RequestId = 1, Ok = true });
        }

        public Task<TReply> RequestAsync<TReply>(Func<int, IpcCommand> build, CancellationToken ct = default)
            where TReply : IpcEvent =>
            Task.FromException<TReply>(new IOException("Not under test."));

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
