using System.Diagnostics;
using System.Globalization;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// Accessibility Insights for Windows' automated checks (its FastPass), run by the axe-windows engine
/// over one window through UI Automation, as Accessibility Insights itself runs them (ST-084 AC2).
///
/// Every finding the engine reports as an error fails the test: those are the rules Accessibility
/// Insights marks as failures, and AC2 asks for none.
///
/// The engine runs in a process of its own (ScreenTail.Tests.AxeRunner) because it skips every element
/// that belongs to the process doing the scan, and the windows under test are this one's: in-process it
/// found nothing at all. Called from the test's thread while the window's thread sits idle in its message
/// loop, so UI Automation calls from the runner are answered. The runner writes nothing to disk and never
/// prints a window's title.
/// </summary>
internal static class AxeScan
{
    public static async Task<List<string>> ErrorsAsync(IntPtr window)
    {
        var runner = Runner();
        var start = new ProcessStartInfo(Host())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(runner);
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(window.ToInt64().ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The axe-windows runner did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The axe-windows scan took more than two minutes.");
        }

        var lines = (await output.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The axe-windows runner exited {process.ExitCode}: {(await error.ConfigureAwait(false)).Trim()}");
        }

        return lines;
    }

    /// <summary>The runner's build output, beside this suite's under the same configuration and framework.</summary>
    private static string Runner()
    {
        var here = AppContext.BaseDirectory;
        var path = Path.Combine(
            here.Replace($"{Path.DirectorySeparatorChar}ScreenTail.Tests.Windows{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}ScreenTail.Tests.AxeRunner{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase),
            "ScreenTail.Tests.AxeRunner.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException($"The axe-windows runner is not built at {path}.");
    }

    /// <summary>The dotnet host running this suite, or the one on the path when the suite runs from its apphost.</summary>
    private static string Host() =>
        Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? self
            : "dotnet";
}
