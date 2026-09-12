using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ScreenTail.Spike.Core;

namespace ScreenTail.Spike.Capture;

/// <summary>The AC1 load test: hooks under concurrent UIA polling, Whisper, screenshots and OCR.</summary>
internal static class LoadRun
{
    public static async Task<int> ExecuteAsync(SpikeOptions options, string outDir)
    {
        var report = new RunReport(options) { LowLevelHooksTimeout = ReadLowLevelHooksTimeout() };
        var callbackMs = new LatencyStats(1_000_000);
        var dispatchMs = new LatencyStats(1_000_000);
        var ring = new SpscRing<InputSample>(1 << 16);
        var monitor = new HookHealthMonitor();

        // Model downloads and warm-up happen before the clock starts.
        using var ocr = options.Ocr ? await OcrWorker.CreateAsync(report) : null;
        using var whisper = options.Whisper ? await WhisperLoad.CreateAsync(report) : null;
        var shots = new ScreenshotPipeline(Path.Combine(outDir, "frames"), ocr, report);
        var pump = new InputPump(ring, shots, report);

        using var hooks = new HookThread(callbackMs, dispatchMs, ring);
        hooks.Start();
        report.MarkStarted();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(options.Minutes));
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        var tasks = new List<Task>
        {
            pump.RunAsync(cts.Token),
            shots.RunAsync(cts.Token),
            WatchAsync(hooks, monitor, report, cts.Token),
            SampleResourcesAsync(report, cts.Token),
        };
        if (ocr is not null)
        {
            tasks.Add(ocr.RunAsync(cts.Token));
        }

        if (options.Uia)
        {
            tasks.Add(new UiaPoller(report).RunAsync(cts.Token));
        }

        if (whisper is not null)
        {
            tasks.Add(whisper.RunAsync(cts.Token));
        }

        if (options.DriveInput)
        {
            report.InputMode = "synthetic (SendInput into the typing target)";
            tasks.Add(InputDriver.RunAsync(report, cts.Token));
        }

        Console.WriteLine(options.DriveInput
            ? $"Recording for {options.Minutes} min with synthetic typing. Don't use the machine meanwhile."
            : $"Recording for {options.Minutes} min. Type and click normally; Ctrl+C ends early.");
        await Task.WhenAll(tasks);

        hooks.Stop();
        Console.CancelKeyPress -= onCancel;

        report.Finish(
            callbackMs.Summarize(RunReport.HookBudgetMs),
            dispatchMs.Summarize(RunReport.HookBudgetMs),
            ring.Dropped,
            monitor);
        Console.WriteLine($"Report: {report.Write(outDir)}");
        return 0;
    }

    private static async Task WatchAsync(HookThread hooks, HookHealthMonitor monitor, RunReport report, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var seconds = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var info = new Native.LastInputInfo { CbSize = (uint)Marshal.SizeOf<Native.LastInputInfo>() };
                if (Native.GetLastInputInfo(ref info))
                {
                    monitor.Check(info.DwTime, hooks.LastHookTick);
                }

                if (++seconds % 30 == 0)
                {
                    Console.WriteLine(report.ProgressLine(seconds));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task SampleResourcesAsync(RunReport report, CancellationToken ct)
    {
        using var process = Process.GetCurrentProcess();
        var lastCpu = process.TotalProcessorTime;
        var lastWall = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var wallMs = Stopwatch.GetElapsedTime(lastWall).TotalMilliseconds;
                var percent = (cpu - lastCpu).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100;
                report.RecordResources(percent, process.WorkingSet64);
                lastCpu = cpu;
                lastWall = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string ReadLowLevelHooksTimeout()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return key?.GetValue("LowLevelHooksTimeout")?.ToString() ?? "not set (Windows default)";
    }
}
