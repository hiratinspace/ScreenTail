using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.Tests.Windows.Accessibility;
using ScreenTail.UI.Controls;
using ScreenTail.UI.Hud;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>
/// The recording clock moves on screen with no new snapshot (ST-072; owner's report, 2026-11-05: "it
/// doesn't count the time, just says recording and the values never change on the small icon").
///
/// The service sends a snapshot when its state changes, not every second. Every earlier test of the pill,
/// the tray and the shell's status line fed one snapshot after another by hand, so the clock always
/// appeared to move. These feed one, then move only a fake clock, and read what is drawn.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RecordingClockOnScreenTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheShellsTrayAndPillCountWithNoNewSnapshot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        StaThread.Run(async () =>
        {
            var time = new TickingTime(Start);
            var service = new RecordingService();
            var file = Onboarded();
            try
            {
                var shell = new LiveShell((_, _) => Task.FromResult<ICaptureChannel>(service), new ShellPreferencesStore(file), time);
                await shell.StartAsync();
                Assert.True(
                    await StaThread.UntilAsync(() => shell.State.Snapshot.Connection == ServiceConnection.Connected && shell.Pill is not null, TimeSpan.FromSeconds(10)),
                    "the shell never connected to the fake service or never built the pill");
                Assert.True(await StaThread.UntilAsync(() => shell.Tray.Tooltip.EndsWith("(0:01)", StringComparison.Ordinal), TimeSpan.FromSeconds(5)), shell.Tray.Tooltip);
                Assert.Equal("00:01", PillClock(shell.Pill!));

                // Seventy seconds and not one message from the service.
                // Seventy seconds and a little more: each tick is aimed just past its second (RecordingClock.PastTheSecond).
                time.Advance(TimeSpan.FromSeconds(70.1));
                await StaThread.UntilAsync(() => shell.Tray.Tooltip.EndsWith("(1:11)", StringComparison.Ordinal), TimeSpan.FromSeconds(5));

                Assert.Equal("ScreenTail · Recording: ScreenConnect (1:11)", shell.Tray.Tooltip);
                Assert.Equal("Recording: ScreenConnect (1:11)", shell.Tray.StateLine);
                Assert.Equal("01:11", PillClock(shell.Pill!));

                // What the store holds is still what the service said: the clock only draws.
                Assert.Equal(1_000, shell.State.Snapshot.Capture!.ElapsedMs);

                shell.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Assert.Equal(0, time.Armed);
            }
            finally
            {
                File.Delete(file);
            }
        });
    }

    [Fact]
    public void TheShellsStatusLineCountsAndItsAnnouncementDoesNot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var state = new ShellState();
        var time = new TickingTime(Start);
        using var clock = new RecordingClock(state, time);
        using var model = new ShellViewModel(state, clock: clock);
        state.Connected(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1", RemoteTool = "rdp", ElapsedMs = 5_000 });
        Assert.Equal("Recording: Remote Desktop (0:05)", model.Status);

        time.Advance(TimeSpan.FromSeconds(4.1));
        Assert.Equal("Recording: Remote Desktop (0:09)", model.Status);
        Assert.Equal("Recording: Remote Desktop", model.StatusAnnouncement);

        state.Observe(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "s1", RemoteTool = "rdp", Reason = "user", ElapsedMs = 9_000 });
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("Paused: Remote Desktop (0:09)", model.Status);
        Assert.Equal("Paused: Remote Desktop", model.StatusAnnouncement);
    }

    /// <summary>The pill's state segment: the TextBlock a screen reader is told about.</summary>
    internal static TextBlock StateSegment(Window pill) =>
        Audit.Descendants(pill).OfType<TextBlock>().Single(t => LiveRegion.GetSetting(t) != AutomationLiveSetting.Off);

    private static string PillClock(Window pill)
    {
        pill.UpdateLayout();
        return StateSegment(pill).Text;
    }

    private static string Onboarded()
    {
        var file = Path.Combine(Path.GetTempPath(), $"screentail-clock-{Guid.NewGuid():N}.json");
        Assert.True(new ShellPreferencesStore(file).Save(new ShellPreferences { OnboardedAt = DateTimeOffset.UnixEpoch }), "the test could not write its preferences file");
        return file;
    }

    /// <summary>A service that said "recording, one second in" once, at the handshake, and nothing since.</summary>
    private sealed class RecordingService : ICaptureChannel
    {
        private volatile bool _disposed;

        public CaptureStateSnapshot State { get; } = new() { State = CaptureStates.Recording, SessionId = "s1", RemoteTool = "screenconnect", ElapsedMs = 1_000 };

        public bool IsConnected => !_disposed;

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

        public Task<CommandResult> SendAsync(Func<int, IpcCommand> build, CancellationToken ct = default) =>
            Task.FromResult(new CommandResult { RequestId = 1, Ok = true });

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

/// <summary>
/// The real pill, in the application's resources, counts on its own and is announced only when its state
/// changes (ST-072, ST-084).
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class PillClockTests(TheApplication app)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ThePillsClockMovesWithNoNewSnapshotStopsWhenPausedAndIsNotAnnouncedEverySecond()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var state = new ShellState();
            var time = new TickingTime(Start);
            using var clock = new RecordingClock(state, time);
            state.Connected(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "s1", ElapsedMs = 5_000 });
            var pill = new HudWindow { DataContext = new HudViewModel(state, clock), WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            pill.Show();
            try
            {
                await Audit.Settle();
                var segment = RecordingClockOnScreenTests.StateSegment(pill);
                Assert.Equal("00:05", segment.Text);

                var heard = await LiveRegionTests.HeardAsync(() => time.Advance(TimeSpan.FromSeconds(3.1)));
                Assert.Equal("00:08", segment.Text);
                Assert.Equal("Recording, 8 seconds", AutomationProperties.GetName(segment));
                Assert.Empty(heard);

                var paused = await LiveRegionTests.HeardAsync(() => state.Observe(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "s1", Reason = "user", ElapsedMs = 8_000 }));
                Assert.Contains("Paused: press Ctrl+Alt+P to resume", paused);

                // Paused, nothing counts and nothing wakes.
                time.Advance(TimeSpan.FromMinutes(1));
                await Audit.Settle();
                Assert.Equal("Paused: press Ctrl+Alt+P to resume", segment.Text);
                Assert.Equal(0, time.Armed);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public async Task ATickFromAnotherThreadReachesTheTrayThePillAndTheStatusLine()
    {
        // The real timer fires on the thread pool. Every other test here moves the fake clock on the UI
        // thread, so the ticks ran there too and the marshalling onto the dispatcher was never exercised
        // (2026-11-05 review). Here the clock is moved from the pool, and the UI thread only waits.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var file = Path.Combine(Path.GetTempPath(), $"screentail-clock-{Guid.NewGuid():N}.json");
        Assert.True(new ShellPreferencesStore(file).Save(new ShellPreferences { OnboardedAt = DateTimeOffset.UnixEpoch }), "the test could not write its preferences file");
        try
        {
            await app.RunAsync(async () =>
            {
                // The shell opens windows of its own as it starts (the diagnostics panel among them); they
                // are this test's to close, so the tests after it find the application as it was.
                var before = Application.Current.Windows.Cast<Window>().ToHashSet();
                var time = new TickingTime(Start);
                var service = new OneSnapshotService();
                var shell = new LiveShell((_, _) => Task.FromResult<ICaptureChannel>(service), new ShellPreferencesStore(file), time);
                await shell.StartAsync();
                Assert.True(
                    await StaThread.UntilAsync(() => shell.State.Snapshot.Connection == ServiceConnection.Connected && shell.Pill is not null, TimeSpan.FromSeconds(10)),
                    "the shell never connected to the fake service or never built the pill");
                using var status = new ShellViewModel(shell.State, clock: shell.Clock);
                try
                {
                    await Task.Run(() => time.Advance(TimeSpan.FromSeconds(70.1)), TestContext.Current.CancellationToken);

                    var arrived = await StaThread.UntilAsync(
                        () => shell.Tray.Tooltip.EndsWith("(1:11)", StringComparison.Ordinal)
                            && RecordingClockOnScreenTests.StateSegment(shell.Pill!).Text == "01:11"
                            && status.Status == "Recording: ScreenConnect (1:11)",
                        TimeSpan.FromSeconds(10));
                    Assert.True(arrived, $"tray '{shell.Tray.Tooltip}', pill '{RecordingClockOnScreenTests.StateSegment(shell.Pill!).Text}', status '{status.Status}'");
                }
                finally
                {
                    await shell.DisposeAsync();
                    foreach (var opened in Application.Current.Windows.Cast<Window>().Where(w => !before.Contains(w)).ToList())
                    {
                        opened.Close();
                    }
                }
            });
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>A service that said "recording, one second in" once, at the handshake, and nothing since.</summary>
    private sealed class OneSnapshotService : ICaptureChannel
    {
        private volatile bool _disposed;

        public CaptureStateSnapshot State { get; } = new() { State = CaptureStates.Recording, SessionId = "s1", RemoteTool = "screenconnect", ElapsedMs = 1_000 };

        public bool IsConnected => !_disposed;

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

        public Task<CommandResult> SendAsync(Func<int, IpcCommand> build, CancellationToken ct = default) =>
            Task.FromResult(new CommandResult { RequestId = 1, Ok = true });

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
