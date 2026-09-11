using System.Globalization;
using System.Runtime;
using ScreenTail.Spike.Capture;

// Physical pixels everywhere, so window bounds, hook coordinates and captures agree on every monitor.
Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorV2);

// Hook callbacks run managed code; keep blocking gen2 collections out of the input path.
GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

var options = SpikeOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(SpikeOptions.Usage);
    return 2;
}

var outDir = options.OutDir ?? Path.Combine(
    Assets.Root,
    "runs",
    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + options.Command);
Directory.CreateDirectory(outDir);
Console.WriteLine($"Output: {outDir}");

return options.Command switch
{
    "run" => await LoadRun.ExecuteAsync(options, outDir),
    "overlay-check" => OverlayCheck.Execute(outDir, options.Label),
    "legibility" => await Legibility.ExecuteAsync(options, outDir),
    _ => 2,
};
