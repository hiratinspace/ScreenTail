using System.Security.Principal;
using System.Text.Json;
using ScreenTail.Core.Capabilities;
using ScreenTail.Service.Capabilities;
using ScreenTail.Service.Capture;
using ScreenTail.Service.Host;

// One capture service per user (ADR-0003). A second copy exits quietly instead of fighting over the pipe.
var userIdentity = OperatingSystem.IsWindows()
    ? WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName
    : Environment.UserName;
using var instance = SingleInstance.TryAcquire(userIdentity);
if (instance is null)
{
    return 3;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("The capture service runs on Windows only.");
    return 2;
}

// Per-monitor DPI awareness, before any window is touched. Without it Windows lies to us about window
// rectangles on a scaled display — a 3840-wide window on a 150% monitor reports 2560 — and every screenshot
// would be captured from the wrong rectangle and stored at the wrong size (ST-025).
Dpi.MakePerMonitorAware();

// `--capabilities` answers "what will this machine actually let ScreenTail do?" and exits. Onboarding
// (ST-083) and the diagnostics panel call the same probe over IPC; this is how CI asks it of real hardware.
if (args.Contains("--capabilities", StringComparer.Ordinal))
{
    var report = new WindowsCapabilityProbe().Probe();
    Console.WriteLine(JsonSerializer.Serialize(report.ToWire(), new JsonSerializerOptions { WriteIndented = true }));
    return report.CanCapture ? 0 : 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<CaptureHost>();
await builder.Build().RunAsync().ConfigureAwait(false);
return 0;
