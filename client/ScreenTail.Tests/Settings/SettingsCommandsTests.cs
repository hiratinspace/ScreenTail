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
        Assert.True(reported.EffectiveLocalOnly);
        Assert.Equal(3, reported.EffectiveRetentionDays);
        Assert.Equal(7, reported.Settings.RetentionDays); // the technician's own, kept for when the policy lets go
    }

    [Fact]
    public async Task ALockedFieldIsReportedAsTheAdminSetIt()
    {
        // The admin locked local-only off over a technician who had it on: sessions upload, so the
        // screen must not say local-only is on (INV-8, INV-11).
        Save(new ClientSettings { LocalOnly = true, RetentionDays = 7 });
        var commands = Commands(new TenantPolicy("p1", 3, false, true, false));

        var reported = Assert.IsType<SettingsReported>(await commands.ReplyToAsync(new GetSettingsCommand { RequestId = 1 }));

        Assert.True(reported.LocalOnlyLocked);
        Assert.False(reported.EffectiveLocalOnly);
        Assert.Equal(3, reported.EffectiveRetentionDays);
        Assert.True(reported.Settings.LocalOnly);
        Assert.Equal(7, reported.Settings.RetentionDays);
    }

    [Fact]
    public async Task AnUnlockedLocalOnlyUnderAnAdminPolicyIsTheTechniciansOwn()
    {
        Save(new ClientSettings { LocalOnly = true, RetentionDays = 7 });
        var commands = Commands(new TenantPolicy("p2", 90, false, false, false));

        var reported = Assert.IsType<SettingsReported>(await commands.ReplyToAsync(new GetSettingsCommand { RequestId = 1 }));

        Assert.False(reported.LocalOnlyLocked);
        Assert.True(reported.EffectiveLocalOnly);
        Assert.True(reported.RetentionLocked);
        Assert.Equal(90, reported.EffectiveRetentionDays); // beyond the technician's 30, in its own field
    }

    [Fact]
    public async Task WithNoAdminPolicyWhatIsInForceIsTheTechniciansOwn()
    {
        Save(new ClientSettings { LocalOnly = true, RetentionDays = 12 });
        var commands = Commands(TenantPolicy.Default);

        var reported = Assert.IsType<SettingsReported>(await commands.ReplyToAsync(new GetSettingsCommand { RequestId = 1 }));

        Assert.False(reported.LocalOnlyLocked);
        Assert.False(reported.RetentionLocked);
        Assert.True(reported.EffectiveLocalOnly);
        Assert.Equal(12, reported.EffectiveRetentionDays);
    }

    [Fact]
    public async Task ThePrivacyScreenShowsTheAdminsValuesAndStillSavesTheTechniciansOwn()
    {
        // Service to screen and back, through the wire format: a 90-day policy that locked local-only
        // off is shown as it is in force, and a save keeps the technician's own values underneath.
        Save(new ClientSettings { LocalOnly = true, RetentionDays = 7 });
        var commands = Commands(new TenantPolicy("p3", 90, false, true, false));
        var panel = new PrivacyPanel(new CommandsGateway(commands));

        await panel.LoadAsync();

        Assert.False(panel.LocalOnly);
        Assert.Equal(90, panel.RetentionDays);
        Assert.Null(panel.Problem);
        panel.Telemetry = true;
        Assert.True(await panel.SaveAsync());
        var stored = new ClientSettingsStore(Path.Combine(_dir, "settings.json")).Load();
        Assert.True(stored.LocalOnly);
        Assert.Equal(7, stored.RetentionDays);
        Assert.True(stored.Telemetry);
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
        var commands = Commands(TenantPolicy.Default);

        var exported = Assert.IsType<AuditExported>(await commands.ReplyToAsync(new ExportAuditCommand { RequestId = 4 }));

        Assert.Contains("\"intact\"", exported.Json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await commands.ReplyToAsync(new StartCommand { RequestId = 5 }));
    }

    private void Save(ClientSettings settings) => Assert.True(new ClientSettingsStore(Path.Combine(_dir, "settings.json")).Save(settings));

    private SettingsCommands Commands(TenantPolicy policy, Action<ClientSettings>? apply = null) => new(
        new ClientSettingsStore(Path.Combine(_dir, "settings.json")),
        () => policy,
        apply ?? (_ => { }),
        _ => Task.FromResult<(IReadOnlyList<AuditRecord>, AuditVerification)>(([], new AuditVerification(true, 0, 0, null))));

    /// <summary>The pipe's gateway without the pipe: each reply goes through the wire framing, as it would between the two processes.</summary>
    private sealed class CommandsGateway(SettingsCommands commands) : IPrivacyGateway
    {
        public async Task<SettingsSnapshot?> LoadAsync(CancellationToken ct = default) =>
            SettingsSnapshot.From(await OverTheWireAsync<SettingsReported>(await commands.ReplyToAsync(new GetSettingsCommand { RequestId = 1 }, ct)));

        public async Task<string?> SaveAsync(ClientSettings settings, CancellationToken ct = default)
        {
            var result = (await commands.HandleAsync(new SetSettingsCommand { RequestId = 2, Settings = settings }, ct))!;
            return result.Ok ? null : result.Error;
        }

        public Task<string?> ExportAuditAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

        private static async Task<T> OverTheWireAsync<T>(IpcEvent? reply)
            where T : IpcEvent
        {
            using var stream = new MemoryStream();
            await IpcFraming.WriteAsync(stream, reply!);
            stream.Position = 0;
            return Assert.IsType<T>(await IpcFraming.ReadAsync<IpcEvent>(stream));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
