using System.Globalization;
using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;

// ST-084. Usage: ScreenTail.Tests.AxeRunner <process id> <window handle>
//
// Scans the window with Accessibility Insights for Windows' automated rules and prints one line per
// error, then exits 0; exits 2 when it could not scan. Nothing is written to disk: the output format is
// None, so no .a11ytest file and no screenshot is made, and the scratch folder axe-windows is given is
// deleted. A line names the element's role, class and automation id, and its name unless it is a window,
// whose name is its title (INV-10 holds for the suite's own output, which the laptop uploads).
if (args.Length != 2
    || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var processId)
    || !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var handle))
{
    Console.Error.WriteLine("Usage: ScreenTail.Tests.AxeRunner <process id> <window handle>");
    return 2;
}

var scratch = Path.Combine(Path.GetTempPath(), "screentail-axe-" + Guid.NewGuid().ToString("N"));
try
{
    var config = Config.Builder.ForProcessId(processId)
        .WithOutputFileFormat(OutputFileFormat.None)
        .WithOutputDirectory(scratch)
        .Build();
    var output = ScannerFactory.CreateScanner(config).Scan(new ScanOptions("st-084", new IntPtr(handle)));
    var windows = 0;
    foreach (var scanned in output.WindowScanOutputs)
    {
        windows++;
        foreach (var error in scanned.Errors)
        {
            Console.WriteLine($"{error.Rule.ID}: {error.Rule.Description} ({Element(error.Element.Properties)})");
        }
    }

    if (windows == 0)
    {
        Console.Error.WriteLine("axe-windows found no window to scan.");
        return 2;
    }

    return 0;
}
catch (AxeWindowsAutomationException failure)
{
    Console.Error.WriteLine($"axe-windows could not scan: {failure.Message}");
    return 2;
}
finally
{
    if (Directory.Exists(scratch))
    {
        Directory.Delete(scratch, recursive: true);
    }
}

static string Element(IReadOnlyDictionary<string, string>? properties)
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
