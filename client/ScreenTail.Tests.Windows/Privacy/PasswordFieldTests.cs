using System.Diagnostics;
using ScreenTail.Core.Privacy;
using ScreenTail.Service.Privacy;

namespace ScreenTail.Tests.Windows.Privacy;

/// <summary>
/// ST-040 against real windows and the real automation stack. <see cref="ScreenTail.Tests"/> covers what
/// the guard decides; this covers whether Windows will actually tell us, which is the part no fake can
/// answer and the part that quietly stops working.
/// </summary>
public sealed class PasswordFieldTests
{
    private static bool OnWindows => OperatingSystem.IsWindows();

    private static bool PerformanceCounts => !string.Equals(
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void APasswordBoxIsRecognisedAndAPlainOneIsNot()
    {
        Assert.SkipUnless(OnWindows, "Windows only.");
        using var window = DesktopWindow.Create("ScreenTail password probe");
        window.AddFields();
        Assert.SkipUnless(window.TakeForeground(), "Could not bring the test window to the front.");
        using var probe = new WindowsFocusedFieldProbe();

        window.Focus(window.PlainField);
        var plain = probe.Read();
        var plainFrom = probe.LastAnswerFrom;

        window.Focus(window.PasswordField);
        var masked = probe.Read();
        var maskedFrom = probe.LastAnswerFrom;

        Measurements.Record(
            $"Focused-field probe: a plain box reads **{plain}** (via {plainFrom}), "
            + $"a password box reads **{masked}** (via {maskedFrom})");

        Assert.Equal(FocusedField.NotPassword, plain);
        Assert.Equal(FocusedField.Password, masked);
    }

    [Fact]
    public void UiAutomationIsTheOneAnsweringAndNotTheFallback()
    {
        // The two mechanisms are not equivalent. EM_GETPASSWORDCHAR can only see a classic edit control, so
        // if automation has quietly stopped answering on this machine, every assertion above still passes
        // while the WPF and browser fields that make up most of a technician's screen go unnoticed. This is
        // the test that would catch that, and the reason the probe reports which one answered.
        Assert.SkipUnless(OnWindows, "Windows only.");
        using var window = DesktopWindow.Create("ScreenTail automation probe");
        window.AddFields();
        Assert.SkipUnless(window.TakeForeground(), "Could not bring the test window to the front.");
        using var probe = new WindowsFocusedFieldProbe();

        window.Focus(window.PasswordField);
        var answer = probe.Read();

        Assert.Equal(FocusedField.Password, answer);
        Assert.Equal(FocusAnswerSource.Automation, probe.LastAnswerFrom);
    }

    [Fact]
    public async Task TheFocusHookFiresWhenFocusMoves()
    {
        Assert.SkipUnless(OnWindows, "Windows only.");
        using var window = DesktopWindow.Create("ScreenTail focus hook");
        window.AddFields();
        Assert.SkipUnless(window.TakeForeground(), "Could not bring the test window to the front.");

        await using var watcher = new WindowsFocusWatcher();
        var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FocusMoved += () => moved.TrySetResult();
        await watcher.StartAsync(TestContext.Current.CancellationToken);
        Assert.SkipUnless(watcher.UsingHook, "The focus hook could not be installed.");

        var start = Stopwatch.GetTimestamp();
        window.Focus(window.PasswordField);
        var fired = await Task.WhenAny(moved.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var took = Stopwatch.GetElapsedTime(start);

        Assert.True(fired == moved.Task, "focus moved to the password box and the hook said nothing");
        Measurements.Record($"Focus change reported to the password guard in **{took.TotalMilliseconds:F0} ms**");
    }

    [Fact]
    public void WhatAskingCosts()
    {
        // This runs on every focus change, and focus changes constantly while someone works. ST-031 budgets
        // 15% of the machine for all of ScreenTail, so the per-ask cost is worth knowing rather than
        // assuming — a cross-process automation call is not obviously cheap.
        Assert.SkipUnless(OnWindows, "Windows only.");
        using var window = DesktopWindow.Create("ScreenTail probe cost");
        window.AddFields();
        Assert.SkipUnless(window.TakeForeground(), "Could not bring the test window to the front.");
        using var probe = new WindowsFocusedFieldProbe();

        _ = probe.Read();   // the first call creates the automation object

        var times = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            window.Focus(i % 2 == 0 ? window.PasswordField : window.PlainField);
            var start = Stopwatch.GetTimestamp();
            _ = probe.Read();
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        times.Sort();
        var median = times[times.Count / 2];
        Measurements.Record(
            $"Asking what has focus: median **{median:F1} ms**, worst {times[^1]:F1} ms "
            + $"(budget 50 ms, enforced: {PerformanceCounts})");

        Assert.SkipUnless(PerformanceCounts, "Timings from a shared cloud runner don't count.");
        Assert.True(median < 50, $"the probe took a median of {median:F1} ms on every focus change");
    }
}
