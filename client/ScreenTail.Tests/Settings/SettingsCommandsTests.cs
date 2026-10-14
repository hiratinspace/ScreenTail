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
    public async Task TwoSavesAtOnceLeaveTheFileAndWhatIsInForceTheSame()
    {
        // Two connections can save at the same moment. Each writes the file and then applies, and the
        // service keeps what was applied in memory, so the second save has to wait until the first has
        // done both. Otherwise the file says one thing and the guard enforces the other until the next save.
        ClientSettings? inForce = null;
        Task<CommandResult?>? second = null;
        var calls = 0;
        SettingsCommands? commands = null;
        commands = Commands(TenantPolicy.Default, settings =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                second = Task.Run(() => commands!.HandleAsync(new SetSettingsCommand { RequestId = 7, Settings = new ClientSettings { LocalOnly = true } }), TestContext.Current.CancellationToken);
                _ = second.Wait(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            }

            inForce = settings;
        });

        var first = await commands.HandleAsync(new SetSettingsCommand { RequestId = 6, Settings = new ClientSettings { LocalOnly = false } }, TestContext.Current.CancellationToken);
        Assert.NotNull(second);
        var then = await second;

        Assert.True(first!.Ok);
        Assert.True(then!.Ok);
        Assert.True(new ClientSettingsStore(Path.Combine(_dir, "settings.json")).Load().LocalOnly);
        Assert.True(inForce!.LocalOnly);
    }

    [Fact]
    public async Task TheAuditLogIsExportedWithItsVerification()
    {
        // Where the file is and what verifying it found. The log itself stays off the pipe (E-2).
        var commands = Commands(TenantPolicy.Default, export: new AuditExportResult("/data/exports/audit.json", 12, new AuditVerification(false, 3, 0, 4), null));

        var exported = Assert.IsType<AuditExported>(await commands.ReplyToAsync(new ExportAuditCommand { RequestId = 4 }));

        Assert.Equal("/data/exports/audit.json", exported.Path);
        Assert.Equal(12, exported.Rows);
        Assert.False(exported.Intact);
        Assert.Contains("row 4 does not follow", exported.Note, StringComparison.Ordinal);
        Assert.Null(await commands.ReplyToAsync(new StartCommand { RequestId = 5 }));
    }

    [Fact]
    public async Task AnExportThatCouldNotBeWrittenIsRefusedWithItsReason()
    {
        var commands = Commands(TenantPolicy.Default, export: AuditExportResult.Refused("The audit log could not be written to disk."));

        Assert.Null(await commands.ReplyToAsync(new ExportAuditCommand { RequestId = 6 }));
        var refused = await commands.HandleAsync(new ExportAuditCommand { RequestId = 6 });

        Assert.False(refused!.Ok);
        Assert.Equal("The audit log could not be written to disk.", refused.Error);

        // Said once. The reason is not kept for a later command that happens to reuse the id.
        Assert.Equal("The audit log could not be exported.", (await commands.HandleAsync(new ExportAuditCommand { RequestId = 6 }))!.Error);
    }

    private SettingsCommands Commands(TenantPolicy policy, Action<ClientSettings>? apply = null, AuditExportResult? export = null) => new(
        new ClientSettingsStore(Path.Combine(_dir, "settings.json")),
        () => policy,
        apply ?? (_ => { }),
        _ => Task.FromResult(export ?? new AuditExportResult("/data/exports/audit.json", 0, new AuditVerification(true, 0, 0, null), null)));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
