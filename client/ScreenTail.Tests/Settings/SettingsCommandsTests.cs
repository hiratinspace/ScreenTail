using ScreenTail.Core.Audit;
using ScreenTail.Core.Net;
using ScreenTail.Core.Settings;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>The service's side of Settings → Privacy (ST-081): read with the locks, save and apply, refuse what cannot be saved, export the audit log.</summary>
public sealed class SettingsCommandsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadingReportsTheSettingsAndWhatTheAdminLocked()
    {
        var commands = Commands(new TenantPolicy("p1", 3, true, true, false));

        var reported = Assert.IsType<SettingsReported>(await commands.ReplyToAsync(new GetSettingsCommand { RequestId = 1 }));

        Assert.True(reported.LocalOnlyLocked);
        Assert.True(reported.RetentionLocked);
        Assert.Equal("p1", reported.PolicyVersion);
        Assert.Equal(7, reported.Settings.RetentionDays);
    }

    [Fact]
    public async Task SavingWritesAppliesAndRefusesABadDocument()
    {
        var applied = new List<ClientSettings>();
        var commands = Commands(TenantPolicy.Default, applied.Add);

        var ok = await commands.HandleAsync(new SetSettingsCommand { RequestId = 2, Settings = new ClientSettings { Telemetry = true, ExcludedProcesses = ["keepass"] } });
        var bad = await commands.HandleAsync(new SetSettingsCommand { RequestId = 3, Settings = new ClientSettings { RetentionDays = 99 } });

        Assert.True(ok!.Ok);
        Assert.False(bad!.Ok);
        Assert.Contains("30 days", bad.Error, StringComparison.Ordinal);
        Assert.True(Assert.Single(applied).Telemetry);
        Assert.True(new ClientSettingsStore(Path.Combine(_dir, "settings.json")).Load().Telemetry);
    }

    [Fact]
    public async Task TheAuditLogIsExportedWithItsVerification()
    {
        var commands = Commands(TenantPolicy.Default);

        var exported = Assert.IsType<AuditExported>(await commands.ReplyToAsync(new ExportAuditCommand { RequestId = 4 }));

        Assert.Contains("\"intact\"", exported.Json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await commands.ReplyToAsync(new StartCommand { RequestId = 5 }));
    }

    private SettingsCommands Commands(TenantPolicy policy, Action<ClientSettings>? apply = null) => new(
        new ClientSettingsStore(Path.Combine(_dir, "settings.json")),
        () => policy,
        apply ?? (_ => { }),
        _ => Task.FromResult<(IReadOnlyList<AuditRecord>, AuditVerification)>(([], new AuditVerification(true, 0, 0, null))));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
