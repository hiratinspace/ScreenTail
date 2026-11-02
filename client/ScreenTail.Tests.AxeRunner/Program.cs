using System.Globalization;
using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;

// ST-084. Usage:
//   ScreenTail.Tests.AxeRunner <process id> <window handle>   scans one window, then exits
//   ScreenTail.Tests.AxeRunner serve <process id>             scans each window handle read from a line of
//                                                             standard input, until the input ends
//
// Scans with Accessibility Insights for Windows' automated rules and prints one line per error. In serve
// mode each scan's lines end with "END", or "ERROR <why>" when it could not scan, so the suite starts one
// process for its whole run rather than one per window. Exits 0, or 2 when it could not scan.
//
// Nothing is written to disk: the output format is None, so no .a11ytest file and no screenshot is made,
// and the scratch folder axe-windows is given is deleted. A line names the element's role, class and
// automation id, and its name unless it is a window, whose name is its title (INV-10 holds for the suite's
// own output, which the laptop uploads).
var serve = args.Length == 2 && args[0] == "serve";
if ((!serve && args.Length != 2)
    || !int.TryParse(args[serve ? 1 : 0], NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
{
    Console.Error.WriteLine("Usage: ScreenTail.Tests.AxeRunner <process id> <window handle> | serve <process id>");
    return 2;
}

var scratch = Path.Combine(Path.GetTempPath(), "screentail-axe-" + Guid.NewGuid().ToString("N"));
try
{
    var config = Config.Builder.ForProcessId(processId)
        .WithOutputFileFormat(OutputFileFormat.None)
        .WithOutputDirectory(scratch)
        .Build();
    var scanner = ScannerFactory.CreateScanner(config);
    if (!serve)
    {
        if (!long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var handle))
        {
            Console.Error.WriteLine("The window handle is not a number.");
            return 2;
        }

        if (Scan(scanner, handle) is { } failure)
        {
            Console.Error.WriteLine(failure);
            return 2;
        }

        return 0;
    }

    while (Console.ReadLine() is { } line)
    {
        if (!long.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var handle))
        {
            Console.WriteLine("ERROR the window handle is not a number");
            continue;
        }

        Console.WriteLine(Scan(scanner, handle) is { } failure ? "ERROR " + failure : "END");
        Console.Out.Flush();
    }

    return 0;
}
finally
{
    if (Directory.Exists(scratch))
    {
        Directory.Delete(scratch, recursive: true);
    }
}

// Prints the window's errors; returns why it could not scan, or null.
static string? Scan(IScanner scanner, long handle)
{
    try
    {
        var output = scanner.Scan(new ScanOptions("st-084", new IntPtr(handle)));
        var windows = 0;
        foreach (var scanned in output.WindowScanOutputs)
        {
            windows++;
            foreach (var error in scanned.Errors)
            {
                // One line per error, whatever an element's name holds, so a scan's output ends where END says.
                Console.WriteLine($"{error.Rule.ID}: {error.Rule.Description} ({Element(error.Element.Properties)})".ReplaceLineEndings(" "));
            }
        }

        return windows == 0 ? "axe-windows found no window to scan" : null;
    }
    catch (AxeWindowsAutomationException failure)
    {
        return $"axe-windows could not scan: {failure.Message}";
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
