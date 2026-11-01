using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// Accessibility Insights for Windows' automated checks (its FastPass), run by the axe-windows engine
/// over one window through UI Automation, as Accessibility Insights itself runs them (ST-084 AC2).
///
/// Every finding the engine reports as an error fails the test: those are the rules Accessibility
/// Insights marks as failures, and AC2 asks for none. Called from the test's thread while the window's
/// thread sits idle in its message loop, which is how UI Automation reaches a window in the same process.
///
/// Nothing is written: the output format is None, so no .a11ytest file and no screenshot is made, and a
/// finding names the element's role, class and automation id, plus its name unless it is a window, whose
/// name is its title (INV-10 holds for the suite's own logs, which the laptop uploads).
/// </summary>
internal static class AxeScan
{
    public static async Task<List<string>> ErrorsAsync(IntPtr window)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "screentail-a11y-" + Guid.NewGuid().ToString("N"));
        try
        {
            return await Task.Run(() =>
            {
                var config = Config.Builder.ForProcessId(Environment.ProcessId)
                    .WithOutputFileFormat(OutputFileFormat.None)
                    .WithOutputDirectory(scratch)
                    .Build();
                var scanner = ScannerFactory.CreateScanner(config);
                var output = scanner.Scan(new ScanOptions("st-084", window));
                var findings = new List<string>();
                foreach (var scanned in output.WindowScanOutputs)
                {
                    foreach (var error in scanned.Errors)
                    {
                        findings.Add($"{error.Rule.ID}: {error.Rule.Description} ({Element(error.Element.Properties)})");
                    }
                }

                return findings;
            }).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private static string Element(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null)
        {
            return "no element";
        }

        string Get(string key) => properties.TryGetValue(key, out var value) ? value : string.Empty;
        var type = Get("ControlType");
        var parts = new List<string> { type, Get("LocalizedControlType"), Get("ClassName") };
        if (!type.Contains("Window", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add($"\"{Get("Name")}\"");
        }

        if (Get("AutomationId") is { Length: > 0 } id)
        {
            parts.Add($"#{id}");
        }

        return string.Join(", ", parts.Where(p => p.Length > 0));
    }
}
