using ScreenTail.Core.Net;
using ScreenTail.Core.Settings;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>The service's side of Settings → Capture (ST-080): read with the tools and what the policy forces, save and apply, refuse what cannot be saved.</summary>
public sealed class CaptureSettingsCommandsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadingReportsTheDocumentWithTheToolsDevicesAndTheForcedScope()
    {
        var commands = Commands(new TenantPolicy("p1", 7, false, false, true));

        var reported = Assert.IsType<CaptureSettingsReported>(await commands.ReplyToAsync(new GetCaptureSettingsCommand { RequestId = 1 }));

        Assert.Equal("rdp", Assert.Single(reported.Tools).Id);
        Assert.Equal("Built-in microphone", Assert.Single(reported.Microphones));
        Assert.True(reported.PolicyForcesAllWindows);
        Assert.Equal(120, reported.DefaultGraceSeconds);
        Assert.True(reported.Settings.AutoStart);
    }

    [Fact]
    public async Task SavingWritesAppliesAndRefusesABadDocument()
    {
        var applied = new List<CaptureSettings>();
        var commands = Commands(TenantPolicy.Default, applied.Add);

        var ok = await commands.HandleAsync(new SetCaptureSettingsCommand { RequestId = 2, Settings = new CaptureSettings { AutoStart = false, GraceSeconds = 60 } });
        var bad = await commands.HandleAsync(new SetCaptureSettingsCommand { RequestId = 3, Settings = new CaptureSettings { Sensitivity = "extreme" } });

        Assert.True(ok!.Ok);
        Assert.False(bad!.Ok);
        Assert.Contains("Low, Medium or High", bad.Error, StringComparison.Ordinal);
        Assert.False(Assert.Single(applied).AutoStart);
        Assert.Equal(60, new CaptureSettingsStore(Path.Combine(_dir, "capture.json")).Load().GraceSeconds);
        Assert.Null(await commands.HandleAsync(new StartCommand { RequestId = 4 }));
    }

    private CaptureSettingsCommands Commands(TenantPolicy policy, Action<CaptureSettings>? apply = null) => new(
        new CaptureSettingsStore(Path.Combine(_dir, "capture.json")),
        () => [new ToolRow("rdp", "Remote Desktop")],
        () => ["Built-in microphone"],
        () => policy,
        () => 120,
        apply ?? (_ => { }));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
