using System.Security.Principal;
using System.Text.Json;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Speech;
using ScreenTail.Platform.Ipc;
using ScreenTail.Service.Capabilities;
using ScreenTail.Service.Capture;
using ScreenTail.Service.Host;
using ScreenTail.Service.Speech;

// Before anything else, because everything after it would already be running alongside whatever was
// loaded. The peer check verifies the file a process started from, and .NET will happily load somebody
// else's code into a genuine signed process if the environment asks — so an attacker needs no forged
// binary, only our real one launched with a startup hook set (ST-012, 2026-09-19 review).
//
// Signed builds refuse. A development build says so and carries on, because these variables are how a
// profiler is attached and a rule that makes debugging impossible is one somebody deletes.
if (RunningHonestly.WhyNotToStart(Signing.IsDevelopmentBuild) is { } refusal)
{
    Console.Error.WriteLine(refusal);
    return 4;
}

foreach (var requested in RunningHonestly.ForeignCodeRequested())
{
    Console.Error.WriteLine($"Warning: {requested} is set, so this process may be running code that is not ScreenTail's.");
}

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

// `--transcribe <file.wav>` runs a recording through the real speech pipeline and prints what it heard.
// ST-027's word-error-rate criterion is measured with it: the transcript goes to research/eval/wer.py
// against the reference text beside the recording.
//
// It exists because the criterion cannot be a unit test. It needs ten minutes of real human narration —
// research/fixtures/audio/README.md says why a synthesised recording would measure the wrong thing — and
// a test that skipped until somebody recorded one would be a skipped test on the one machine whose
// answers count (ST-018).
if (Array.IndexOf(args, "--transcribe") is var flag and >= 0)
{
    if (flag + 1 >= args.Length)
    {
        Console.Error.WriteLine("Usage: --transcribe <recording.wav> [--model tiny.en|base.en|small.en]");
        return 2;
    }

    return await Transcribe.RunAsync(args[flag + 1], ModelFrom(args)).ConfigureAwait(false);
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<CaptureHost>();
await builder.Build().RunAsync().ConfigureAwait(false);
return 0;


static SpeechModel ModelFrom(string[] args)
{
    var at = Array.IndexOf(args, "--model");
    var name = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    return SpeechModels.All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? SpeechModels.For(Environment.ProcessorCount);
}
