using System.Diagnostics;
using System.Drawing;
using FlaUI.UIA3;
using ScreenTail.Spike.Core;

namespace ScreenTail.Spike.Capture;

/// <summary>
/// Polls the focused UIA element every 250 ms on its own MTA thread (AC2) and adds UIA load for AC1.
/// Records process name, classes and control types only; never element names or window titles.
/// </summary>
internal sealed class UiaPoller(RunReport report)
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    public Task RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Loop(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Loop(CancellationToken ct)
    {
        using var automation = new UIA3Automation();
        UiaTransition? last = null;

        while (!ct.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var hwnd = Native.GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    var window = automation.FromHandle(hwnd);
                    var focused = automation.FocusedElement();
                    var snapshot = new UiaFocusSnapshot(
                        window.Properties.ClassName.ValueOrDefault ?? string.Empty,
                        focused.Properties.ClassName.ValueOrDefault ?? string.Empty,
                        focused.Properties.ControlType.ValueOrDefault.ToString(),
                        focused.FindAllChildren().Length,
                        AreaFraction(
                            focused.Properties.BoundingRectangle.ValueOrDefault,
                            window.Properties.BoundingRectangle.ValueOrDefault));
                    report.RecordUiaQuery(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                    var transition = new UiaTransition(
                        DateTime.Now,
                        ProcessName(hwnd),
                        snapshot,
                        OpaqueSubtreeClassifier.Classify(snapshot));
                    if (last is null || !transition.SameStateAs(last))
                    {
                        report.AddUiaTransition(transition);
                        last = transition;
                    }
                }
            }
            catch (Exception ex)
            {
                // Elements vanish mid-query all the time; count and keep polling.
                report.CountUiaError(ex.GetType().Name);
            }

            ct.WaitHandle.WaitOne(Interval);
        }
    }

    private static double AreaFraction(Rectangle element, Rectangle window)
    {
        var windowArea = (double)window.Width * window.Height;
        return windowArea <= 0 ? 0 : Math.Clamp((double)element.Width * element.Height / windowArea, 0, 1);
    }

    private static string ProcessName(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "(unknown)";
        }
    }
}
