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
/// found nothing at all. One runner serves the whole suite, a window handle per line, so a scan costs a
/// scan and not a process start. Called from the test's thread while the window's thread sits idle in its
/// message loop, so UI Automation calls from the runner are answered. The runner writes nothing to disk
/// and never prints a window's title.
/// </summary>
internal static class AxeScan
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static Process? _runner;
    private static int _stopsOnExit;

    /// <summary>How many scans finished, and the time they took, for the suite's cost line.</summary>
    public static int Scans { get; private set; }

    public static TimeSpan Spent { get; private set; }

    public static async Task<List<string>> ErrorsAsync(IntPtr window)
    {
        await OneAtATime.WaitAsync().ConfigureAwait(false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var runner = Runner();
            await runner.StandardInput.WriteLineAsync(window.ToInt64().ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            await runner.StandardInput.FlushAsync().ConfigureAwait(false);
            var findings = new List<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            while (true)
            {
                string? line;
                try
                {
                    line = await runner.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Stop();
                    throw new TimeoutException("The axe-windows scan took more than two minutes.");
                }

                if (line is null)
                {
                    var why = runner.HasExited ? $"exited {runner.ExitCode}" : "closed its output";
                    Stop();
                    throw new InvalidOperationException($"The axe-windows runner {why} during a scan.");
                }

                if (line == "END")
                {
                    Scans++;
                    Spent += clock.Elapsed;
                    return findings;
                }

                if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The axe-windows runner could not scan: {line[6..]}");
                }

                findings.Add(line);
            }
        }
        finally
        {
            _ = OneAtATime.Release();
        }
    }

    /// <summary>Ends the runner. The next scan starts another.</summary>
    public static void Stop()
    {
        if (Interlocked.Exchange(ref _runner, null) is { } runner)
        {
            try
            {
                if (!runner.HasExited)
                {
                    runner.StandardInput.Close();
                    if (!runner.WaitForExit(5_000))
                    {
                        runner.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            runner.Dispose();
        }
    }

    private static Process Runner()
    {
        if (_runner is { HasExited: false } running)
        {
            return running;
        }

        Stop();
        var start = new ProcessStartInfo(Host())
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path());
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        _runner = Process.Start(start) ?? throw new InvalidOperationException("The axe-windows runner did not start.");
        if (Interlocked.Exchange(ref _stopsOnExit, 1) == 0)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        }

        return _runner;
    }

    /// <summary>The runner's build output, beside this suite's under the same configuration and framework.</summary>
    private static string Path()
    {
        var here = AppContext.BaseDirectory;
        var path = System.IO.Path.Combine(
            here.Replace($"{System.IO.Path.DirectorySeparatorChar}ScreenTail.Tests.Windows{System.IO.Path.DirectorySeparatorChar}", $"{System.IO.Path.DirectorySeparatorChar}ScreenTail.Tests.AxeRunner{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase),
            "ScreenTail.Tests.AxeRunner.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException($"The axe-windows runner is not built at {path}.");
    }

    /// <summary>The dotnet host running this suite, or the one on the path when the suite runs from its apphost.</summary>
    private static string Host() =>
        Environment.ProcessPath is { } self && System.IO.Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? self
            : "dotnet";
}
