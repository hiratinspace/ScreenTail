using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.Versioning;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Performance;

/// <summary>
/// ST-031's budgets for the capture service, measured on the laptop: idle CPU under 1%, recording CPU under
/// 15% of a four-core machine, and under 40 MB of disk per twenty-minute session.
///
/// Each test measures a baseline of this test host on its own first, then composes the service's parts
/// (<see cref="ServiceRig"/>), lets them settle, and measures again over slices, so the figure is what the
/// service's code adds and the spread across slices is in the report. Nothing here depends on a timer
/// firing on time: every rate is divided by the time that actually passed.
///
/// Processor time is counted in cycles (<see cref="CycleCounter"/>). Windows' own figure, the one Task
/// Manager shows, charges a whole 15.625 ms clock tick to whichever thread was running when the tick
/// came, which is the right total for a busy process and a coin toss for an idle one; it is in the report
/// beside the cycle figure.
///
/// They run everywhere and skip nowhere: the skip budgets are full (docs/dev/windows-test-loop.md). On a
/// hosted runner the windows are a few seconds, the numbers are reported and nothing is enforced, because a
/// shared cloud VM's timings say nothing about a technician's laptop.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ServiceCostTests
{
    /// <summary>ST-031: idle CPU under 1%. Held against one core, which is stricter than the budget read as a share of the machine; see docs/dev/performance.md.</summary>
    private const double IdleBudgetPercentOfOneCore = 1.0;

    /// <summary>ST-031: recording CPU under 15% of a four-core machine, which is 60% of one core on any machine.</summary>
    private const double RecordingBudgetPercentOfOneCore = 15.0 * 4;

    /// <summary>ST-005 and ST-031: under 40 MB for a twenty-minute session.</summary>
    private const double DiskBudgetMegabytesPerTwentyMinutes = 40;

    /// <summary>
    /// The busy twenty-minute session the Mac suite's disk test assumes (SessionStoreTests): 25 click frames
    /// and ST-026's cap of 60 scene frames. The disk budget is held against that many frames of the size
    /// measured here, because the rate this test stages at is its own choice and the size is the fact.
    /// </summary>
    private const int BusySessionFrames = 25 + 60;

    private static bool Counts => Hardware.PerformanceCounts;

    private static TimeSpan Slice => Counts ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(500);

    private static TimeSpan SettleFor => Counts ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(1);

    [Fact]
    public async Task WithNoSessionTheServiceIsAlmostSilent()
    {
        Hardware.RequireDesktop();
        var ct = TestContext.Current.CancellationToken;

        ProcessCost.Settle();
        var host = await ProcessCost.MeasureAsync(Slice, 3, ct);

        await using var rig = await ServiceRig.StartAsync(scope: null, ct);
        await Task.Delay(SettleFor, ct);
        ProcessCost.Settle();
        var idle = await ProcessCost.MeasureAsync(Slice, 6, ct);

        Assert.Equal(SessionState.Idle, rig.Machine.State);
        Assert.False(rig.Hooks.Installed, "the input hooks were in with no session (ST-031 keeps them out until a session starts)");

        var cpu = idle.MedianCpu - host.MedianCpu;
        var wakeups = idle.MedianWakeups - host.MedianWakeups;
        Measurements.Record(string.Create(
            CultureInfo.InvariantCulture,
            $"Service at idle, no session, {idle.Elapsed.TotalSeconds:F0} s in {Slice.TotalSeconds:F0} s slices after {SettleFor.TotalSeconds:F0} s to settle: "
            + $"**{cpu:F3}%** of one core net of the test host, median slice against median slice ({cpu / Environment.ProcessorCount:F3}% of {Environment.ProcessorCount} logical processors; "
            + $"service {CostWindow.Spread(idle.CpuBySlice, "F3")}, host alone {CostWindow.Spread(host.CpuBySlice, "F3")}; by clock ticks, as Task Manager counts, service {CostWindow.Spread(idle.TickCpuBySlice, "F3")}), "
            + $"**{wakeups:F1} wakeups/s** net (service {CostWindow.Spread(idle.WakeupsBySlice, "F1")}, host alone {CostWindow.Spread(host.WakeupsBySlice, "F1")}; {idle.ThreadsWoken()}), "
            + $"private memory {ProcessCost.Megabytes(idle.Last.PrivateBytes - host.Last.PrivateBytes)} over the host's {ProcessCost.Megabytes(host.Last.PrivateBytes)} "
            + $"(budget {IdleBudgetPercentOfOneCore:F0}% of one core, enforced: {Counts}; wakeups and memory reported, not enforced)"));

        Assert.True(
            !Counts || cpu < IdleBudgetPercentOfOneCore,
            $"the idle service used {cpu:F3}% of one core net of the host, budget {IdleBudgetPercentOfOneCore:F0}%");
    }

    [Fact]
    public async Task ARecordingSessionStaysInsideItsProcessorAndDiskBudgets()
    {
        // A steady session without speech: hooks in, the click drain and the scene sampler running against a
        // window that changes every two seconds, and a text-heavy frame staged every six, the rate of a
        // technician clicking through a settings dialog. Every frame goes through the real OCR, masker,
        // encryption and store. Speech is the one part missing, because nobody talks in a CI run; ADR-0001
        // measured it on this laptop and SpeechUnloadTests measures what it holds.
        Hardware.RequireScreenCapture();
        var ct = TestContext.Current.CancellationToken;
        using var window = DesktopWindow.Create("ScreenTail recording cost target");
        window.Maximize();
        window.RequireForeground();
        window.ShowContrast(ContrastLayout.SideBySide);

        // Drawn before anything is measured, so the test's own drawing is not counted as the service's.
        var frames = Enumerable.Range(0, 24).Select(RenderDialog).ToList();

        ProcessCost.Settle();
        var host = await ProcessCost.MeasureAsync(Slice, 3, ct);

        var scope = new ScopeDecision(CaptureScope.RemoteTool, "rdp", RemoteToolKind.Rdp, "Capturing", window.Handle);
        await using var rig = await ServiceRig.StartAsync(() => scope, ct);
        var started = DateTimeOffset.UtcNow;
        var bytesAtStart = rig.StoreBytes();
        Assert.True(await rig.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, localOnly: true, ct: ct), "the session did not start");

        var staged = 0;
        using var working = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workload = Task.Run(
            async () =>
            {
                using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));
                for (var second = 1; await tick.WaitForNextTickAsync(working.Token); second++)
                {
                    if (second % 2 == 0)
                    {
                        window.ShowContrast(second % 4 == 0 ? ContrastLayout.SideBySide : ContrastLayout.Stacked);
                    }

                    if (second % 6 == 1)
                    {
                        var image = frames[staged % frames.Count];
                        var frame = new StagedFrame(Guid.NewGuid().ToString("N"), rig.Machine.NowMs, FrameTrigger.Click, 1920, 1080, null, image);
                        if (await rig.Machine.TryStageFrameAsync(frame, working.Token))
                        {
                            staged++;
                        }
                    }
                }
            },
            CancellationToken.None);

        try
        {
            await Task.Delay(Slice, ct);
            var recording = await ProcessCost.MeasureAsync(Slice * 2, 6, ct);
            await working.CancelAsync();
            await IgnoringCancellation(workload);
            var minutes = (DateTimeOffset.UtcNow - started).TotalMinutes;

            Assert.True(rig.Hooks.Installed, "the input hooks were not in during the session");
            Assert.Equal(SessionState.Recording, rig.Machine.State);

            // Everything staged is written or let go before the disk is read.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (rig.Pending.Depth > 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
            }

            var progress = rig.Redaction.Progress;
            var session = (await rig.Store.LoadSessionAsync(rig.Machine.SessionId!, ct))!;
            var stored = session.Frames.Count;
            var grew = rig.StoreBytes() - bytesAtStart;
            var perMinute = grew / minutes;
            var perTwenty = perMinute * 20 / (1024 * 1024);
            var perFrame = stored > 0 ? grew / (double)stored : 0;
            var fit = perFrame > 0 ? DiskBudgetMegabytesPerTwentyMinutes * 1024 * 1024 / perFrame : double.PositiveInfinity;
            var busy = perFrame * BusySessionFrames / (1024 * 1024);
            var scenes = rig.Scenes.Counts;

            var cpu = recording.MedianCpu - host.MedianCpu;
            Measurements.Record(string.Create(
                CultureInfo.InvariantCulture,
                $"Service recording (no speech), {recording.Elapsed.TotalSeconds:F0} s in {(Slice * 2).TotalSeconds:F0} s slices: **{cpu:F2}%** of one core net of the test host, median slice against median slice "
                + $"({cpu / Environment.ProcessorCount:F2}% of {Environment.ProcessorCount} logical processors; service {CostWindow.Spread(recording.CpuBySlice)}, host alone {CostWindow.Spread(host.CpuBySlice)}; by clock ticks, service {CostWindow.Spread(recording.TickCpuBySlice)}), "
                + $"{recording.MedianWakeups - host.MedianWakeups:F0} wakeups/s net ({recording.ThreadsWoken()}), "
                + $"private memory {ProcessCost.Megabytes(recording.Last.PrivateBytes - host.Last.PrivateBytes)} over the host "
                + $"(budget {RecordingBudgetPercentOfOneCore:F0}% of one core = 15% of four cores, enforced: {Counts})"));
            Measurements.Record(string.Create(
                CultureInfo.InvariantCulture,
                $"Recording workload: {staged} text frames staged, {scenes.Kept} scene frames of {scenes.Seen} samples, "
                + $"{progress.Frames} redacted ({progress.Unread} with no words, discarded), a mean of {progress.PerFrame.TotalMilliseconds:F0} ms a frame, slowest {progress.Slowest.TotalMilliseconds:F0} ms"));
            Measurements.Record(string.Create(
                CultureInfo.InvariantCulture,
                $"Disk: the store grew {ProcessCost.Megabytes(grew)} over {minutes:F1} min for {stored} stored frames, "
                + $"**{perFrame / 1024:F0} KB a frame**, {ProcessCost.Megabytes((long)perMinute)} per session-minute at this rate, "
                + $"{perTwenty:F1} MB per 20 minutes at this test's rate of one text frame every 6 s; "
                + $"the busy session of {BusySessionFrames} frames comes to **{busy:F1} MB** (budget {DiskBudgetMegabytesPerTwentyMinutes:F0} MB, enforced: {Counts}), "
                + $"and the budget holds {fit:F0} frames of this size"));

            Assert.True(stored > 0, "no frame reached the store, so the disk figure measured nothing");
            Assert.True(
                !Counts || cpu < RecordingBudgetPercentOfOneCore,
                $"recording used {cpu:F2}% of one core net of the host, budget {RecordingBudgetPercentOfOneCore:F0}%");
            Assert.True(
                !Counts || busy < DiskBudgetMegabytesPerTwentyMinutes,
                $"{BusySessionFrames} frames of {perFrame / 1024:F0} KB come to {busy:F1} MB, budget {DiskBudgetMegabytesPerTwentyMinutes:F0} MB for twenty minutes");
        }
        finally
        {
            await working.CancelAsync();
            await IgnoringCancellation(workload);
        }
    }

    private static async Task IgnoringCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// A printer's properties dialog at 1920x1080, different on every frame so nothing downstream can answer
    /// twice from a cache. No sign-in words: a frame the login heuristic took for a credential prompt would
    /// suppress the session and measure a pause instead of a recording.
    /// </summary>
    private static byte[] RenderDialog(int index)
    {
        using var bitmap = new Bitmap(1920, 1080, PixelFormat.Format24bppRgb);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(243, 243, 243));
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var heading = new Font("Segoe UI", 16, FontStyle.Bold);
        using var body = new Font("Segoe UI", 11);
        using var ink = new SolidBrush(Color.FromArgb(28, 28, 28));
        using var panel = new SolidBrush(Color.FromArgb(225, 230, 238));

        graphics.FillRectangle(panel, 1100, 100, 700, 800);
        graphics.DrawString(string.Create(CultureInfo.InvariantCulture, $"Printer Properties ({index})"), heading, ink, 60, 40);
        var y = 110;
        foreach (var label in new[]
        {
            "General", "Sharing", "Ports", "Advanced", "Colour Management", "Device Settings",
            "Location: Floor 2 east", "Comment: shared queue", "Model: LaserJet 4250", "Paper available: A4",
            "Print Test Page", "Apply",
        })
        {
            graphics.DrawString(label, body, ink, 100, y);
            graphics.DrawString(string.Create(CultureInfo.InvariantCulture, $"{label} {index}"), body, ink, 1140, y);
            y += 34;
        }

        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Jpeg);
        return buffer.ToArray();
    }
}
