using ScreenTail.Core.Net;
using ScreenTail.Core.Settings;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// Settings → Privacy &amp; Redaction (ST-081, Spec §5 S6): the technician's own settings, validated
/// before they are saved, with the fields the admin's policy locked shown read-only and enforced
/// (INV-11); a custom pattern checked as it is typed with a test box; the exact list of fields
/// telemetry sends; export and delete. The service is a fake here; <c>PipePrivacyTests</c> is the real one.
/// </summary>
public sealed class PrivacyPanelTests
{
    [Fact]
    public async Task LoadShowsTheSettingsAndWhichFieldsTheAdminLocked()
    {
        var gateway = new FakePrivacy
        {
            Snapshot = new SettingsSnapshot(
                new ClientSettings { LocalOnly = false, RetentionDays = 7, Telemetry = false },
                LocalOnlyLocked: true,
                RetentionLocked: true,
                PolicyVersion: "p20260925",
                EffectiveLocalOnly: true,
                EffectiveRetentionDays: 3),
        };
        var panel = new PrivacyPanel(gateway);

        await panel.LoadAsync();

        Assert.True(panel.LocalOnly);
        Assert.True(panel.LocalOnlyLocked);
        Assert.Equal(3, panel.RetentionDays);
        Assert.True(panel.RetentionLocked);
        Assert.Equal("Set by your admin (policy p20260925)", panel.LockedBecause);
        Assert.False(panel.Telemetry);
    }

    [Fact]
    public async Task ALockedFieldIsNotSentEvenIfTheScreenTriesTo()
    {
        var gateway = new FakePrivacy
        {
            Snapshot = new SettingsSnapshot(new ClientSettings { LocalOnly = true, RetentionDays = 3 }, LocalOnlyLocked: true, RetentionLocked: true, PolicyVersion: "p1", EffectiveLocalOnly: false, EffectiveRetentionDays: 14),
        };
        var panel = new PrivacyPanel(gateway);
        await panel.LoadAsync();

        panel.LocalOnly = !panel.LocalOnly;
        panel.RetentionDays = 30;
        Assert.True(await panel.SaveAsync());

        // Neither the screen's attempt nor the admin's value is written over the technician's own.
        Assert.True(gateway.Saved!.LocalOnly);
        Assert.Equal(3, gateway.Saved.RetentionDays);
    }

    [Fact]
    public async Task AnUnlockedFieldShowsTheTechniciansOwnValue()
    {
        var gateway = new FakePrivacy
        {
            Snapshot = new SettingsSnapshot(new ClientSettings { LocalOnly = true, RetentionDays = 12 }, LocalOnlyLocked: false, RetentionLocked: false, PolicyVersion: "default", EffectiveLocalOnly: true, EffectiveRetentionDays: 12),
        };
        var panel = new PrivacyPanel(gateway);
        await panel.LoadAsync();

        Assert.True(panel.LocalOnly);
        Assert.Equal(12, panel.RetentionDays);
        Assert.Equal(ClientSettings.MaxRetentionDays, panel.RetentionMaximum);
        panel.LocalOnly = false;
        panel.RetentionDays = 30;
        Assert.True(await panel.SaveAsync());
        Assert.False(gateway.Saved!.LocalOnly);
        Assert.Equal(30, gateway.Saved.RetentionDays);
    }

    [Fact]
    public async Task ALockedFieldKeepsShowingTheAdminsValueWhenTheScreenWritesToIt()
    {
        // A slider whose top shrinks under its value pushes the clamped value back through its binding;
        // a locked field must still show what is in force, not what the screen wrote.
        var gateway = new FakePrivacy
        {
            Snapshot = new SettingsSnapshot(new ClientSettings { LocalOnly = true, RetentionDays = 7 }, LocalOnlyLocked: true, RetentionLocked: true, PolicyVersion: "p1", EffectiveLocalOnly: false, EffectiveRetentionDays: 3),
        };
        var panel = new PrivacyPanel(gateway);
        await panel.LoadAsync();

        panel.LocalOnly = true;
        panel.RetentionDays = 30;

        Assert.False(panel.LocalOnly);
        Assert.Equal(3, panel.RetentionDays);
        Assert.Equal(ClientSettings.MaxRetentionDays, panel.RetentionMaximum);
    }

    [Fact]
    public async Task ReloadingFollowsThePolicyAndLiftingTheLockBringsBackTheTechniciansOwnValues()
    {
        // The Settings pane is kept and reloaded on every visit, so one panel sees the policy change.
        var own = new ClientSettings { LocalOnly = true, RetentionDays = 7 };
        var gateway = new FakePrivacy
        {
            Snapshot = new SettingsSnapshot(own, LocalOnlyLocked: true, RetentionLocked: true, PolicyVersion: "p1", EffectiveLocalOnly: false, EffectiveRetentionDays: 90),
        };
        var panel = new PrivacyPanel(gateway);
        await panel.LoadAsync();
        Assert.Equal(90, panel.RetentionDays);
        Assert.Equal(90, panel.RetentionMaximum);
        Assert.False(panel.LocalOnly);

        gateway.Snapshot = new SettingsSnapshot(own, LocalOnlyLocked: true, RetentionLocked: true, PolicyVersion: "p2", EffectiveLocalOnly: false, EffectiveRetentionDays: 3);
        await panel.LoadAsync();
        panel.RetentionDays = ClientSettings.MaxRetentionDays; // what a shrinking slider would push back
        Assert.Equal(3, panel.RetentionDays);
        Assert.Equal(ClientSettings.MaxRetentionDays, panel.RetentionMaximum);
        Assert.Equal("Set by your admin (policy p2)", panel.LockedBecause);

        gateway.Snapshot = new SettingsSnapshot(own, LocalOnlyLocked: false, RetentionLocked: false, PolicyVersion: "default", EffectiveLocalOnly: true, EffectiveRetentionDays: 7);
        await panel.LoadAsync();
        Assert.False(panel.LocalOnlyLocked);
        Assert.False(panel.RetentionLocked);
        Assert.True(panel.LocalOnly);
        Assert.Equal(7, panel.RetentionDays);
        Assert.Equal(ClientSettings.MaxRetentionDays, panel.RetentionMaximum);
        Assert.Null(panel.Problem);

        panel.Telemetry = true;
        Assert.True(await panel.SaveAsync());
        Assert.True(gateway.Saved!.LocalOnly);
        Assert.Equal(7, gateway.Saved.RetentionDays);
    }

    [Fact]
    public async Task ALockedRetentionLongerThanTheTechnicianMayChooseIsShownWholeAndDoesNotBlockSaving()
    {
        var gateway = new FakePrivacy
        {
            Snapshot = new SettingsSnapshot(new ClientSettings { RetentionDays = 7 }, LocalOnlyLocked: false, RetentionLocked: true, PolicyVersion: "p1", EffectiveLocalOnly: false, EffectiveRetentionDays: 365),
        };
        var panel = new PrivacyPanel(gateway);
        await panel.LoadAsync();

        Assert.Equal(365, panel.RetentionDays);
        Assert.Equal(365, panel.RetentionMaximum);
        Assert.Null(panel.Problem);
        Assert.True(await panel.SaveAsync());
        Assert.Equal(7, gateway.Saved!.RetentionDays);
    }

    [Theory]
    [InlineData("(", false)]
    [InlineData("", false)]
    [InlineData(".*", false)]
    [InlineData(@"ACME-\d{6}", true)]
    public async Task ACustomPatternIsCheckedAsItIsTypedAndABadOneBlocksAddingIt(string pattern, bool ok)
    {
        var panel = new PrivacyPanel(new FakePrivacy());
        await panel.LoadAsync();

        panel.NewPattern = pattern;

        Assert.Equal(ok, panel.NewPatternProblem is null);
        Assert.Equal(ok, panel.CanAddPattern);
        Assert.Equal(ok, panel.AddPattern());
        Assert.Equal(ok ? 1 : 0, panel.CustomPatterns.Count);
    }

    [Fact]
    public async Task TheTestBoxShowsWhatAPatternWouldMask()
    {
        var panel = new PrivacyPanel(new FakePrivacy());
        await panel.LoadAsync();
        panel.NewPattern = @"ACME-\d{6}";

        panel.Sample = "Ticket ACME-482130 for the front desk";

        Assert.Equal("Ticket [REDACTED] for the front desk", panel.SampleMasked);
    }

    [Fact]
    public async Task AnInvalidPatternAlreadyInTheListBlocksSaveAndSaysWhy()
    {
        // A pattern that was valid under an older engine, or edited by hand in the file: the screen
        // says so and will not save over it (AC1).
        var gateway = new FakePrivacy { Snapshot = new SettingsSnapshot(new ClientSettings { CustomPatterns = ["("] }, false, false, "default", false, 7) };
        var panel = new PrivacyPanel(gateway);
        await panel.LoadAsync();

        Assert.Contains("regular expression", panel.Problem, StringComparison.Ordinal);
        Assert.False(panel.CanSave);
        Assert.False(await panel.SaveAsync());
        Assert.Null(gateway.Saved);
    }

    [Fact]
    public async Task TheTelemetryListIsExactlyWhatTheMetricSends()
    {
        var panel = new PrivacyPanel(new FakePrivacy());
        await panel.LoadAsync();

        Assert.Equal(
            ["session_id", "started_at", "duration_ms", "frames", "transcript_segments", "frames_purged_unredacted", "edit_ratio", "published"],
            PrivacyPanel.TelemetryFields);
    }

    [Fact]
    public async Task ExcludedProcessesAreNamesNotPaths()
    {
        var panel = new PrivacyPanel(new FakePrivacy());
        await panel.LoadAsync();

        panel.NewProcess = @"C:\Program Files\KeePass\KeePass.exe";
        Assert.NotNull(panel.NewProcessProblem);
        panel.NewProcess = "keepass";
        Assert.True(panel.AddProcess());

        Assert.Equal(["keepass"], panel.ExcludedProcesses);
        Assert.True(await panel.SaveAsync());
    }

    [Fact]
    public async Task TheServicesRefusalIsShownAndNothingIsPretended()
    {
        var gateway = new FakePrivacy { Refusal = "Not connected to the capture service." };
        var panel = new PrivacyPanel(gateway);
        await panel.LoadAsync();
        panel.Telemetry = true;

        Assert.False(await panel.SaveAsync());

        Assert.Equal("Not connected to the capture service.", panel.Problem);
    }

    private sealed class FakePrivacy : IPrivacyGateway
    {
        public SettingsSnapshot? Snapshot { get; set; } = new(new ClientSettings(), false, false, "default", false, 7);

        public string? Refusal { get; set; }

        public ClientSettings? Saved { get; private set; }

        public Task<SettingsSnapshot?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);

        public Task<string?> SaveAsync(ClientSettings settings, CancellationToken ct = default)
        {
            if (Refusal is null)
            {
                Saved = settings;
            }

            return Task.FromResult(Refusal);
        }

        public Task<string?> ExportAuditAsync(CancellationToken ct = default) => Task.FromResult<string?>("{}");
    }
}
