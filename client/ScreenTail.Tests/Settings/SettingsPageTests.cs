using ScreenTail.Core.Settings;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Update;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// Settings' one Save bar (Spec §1.1 and the Settings layout, owner decision): every section's changes
/// are counted together, Save writes each changed section through its own save path, a section that
/// fails is named and keeps its changes pending while the others commit, Undo puts every section back,
/// and a field the admin's policy locked is never a pending change (INV-11). The services are fakes.
/// </summary>
public sealed class SettingsPageTests
{
    [Fact]
    public async Task NothingIsPendingUntilAFieldChangesAndThenEverySectionIsCountedTogether()
    {
        var (page, capture, privacy, _, _) = await LoadedAsync();

        Assert.False(page.HasPendingChanges);
        Assert.Equal(SaveBarState.Hidden, page.State);
        Assert.Equal(string.Empty, page.Summary);

        capture.AutoStart = false;
        privacy.Telemetry = true;
        privacy.RetentionDays = 14;

        Assert.True(page.HasPendingChanges);
        Assert.Equal(SaveBarState.Pending, page.State);
        Assert.Equal(3, page.PendingCount);
        Assert.Equal(1, page.PendingIn("Capture"));
        Assert.Equal(2, page.PendingIn("Privacy"));
        Assert.Equal("3 changes not saved:", page.Summary);
        Assert.Equal("Capture", page.FirstChanged!.Name);
        Assert.Equal(
            [("Capture", "Starting and stopping"), ("Privacy", "Retention"), ("Privacy", "Usage telemetry")],
            page.AllChanges.Select(c => (c.Section, c.Change)).OrderBy(c => c.Section, StringComparer.Ordinal).ThenBy(c => c.Change, StringComparer.Ordinal));

        // Back to what was loaded is no change at all.
        capture.AutoStart = true;
        Assert.Equal(2, page.PendingCount);
        Assert.Equal("Privacy", page.FirstChanged!.Name);
    }

    [Fact]
    public async Task OneChangeIsSaidInTheSingular()
    {
        var (page, capture, _, _, _) = await LoadedAsync();

        capture.Sensitivity = "high";

        Assert.Equal("1 change not saved:", page.Summary);
        Assert.Equal(["Screenshots"], page.Sections.Single(s => s.Name == "Capture").Changes);
    }

    [Fact]
    public async Task SaveWritesEveryChangedSectionThroughItsOwnSavePath()
    {
        var startAtLogin = new List<bool>();
        var (page, capture, privacy, gateways, updates) = await LoadedAsync(startAtLogin.Add);

        capture.StartUiAtLogin = true;
        privacy.Telemetry = true;
        updates.Choose(UpdateChannel.Pilot);
        Assert.Equal(3, page.PendingCount);

        Assert.True(await page.SaveAsync());

        Assert.True(gateways.Capture.Saved!.StartUiAtLogin);
        Assert.True(gateways.Privacy.Saved!.Telemetry);
        Assert.Equal(UpdateChannel.Pilot, gateways.Updates.Chosen);

        // Capture's save path includes the shell's Run key, as its own Save button did.
        Assert.Equal([true], startAtLogin);
        Assert.False(page.HasPendingChanges);
        Assert.Equal(SaveBarState.Hidden, page.State);
        Assert.Empty(page.Failures);
    }

    [Fact]
    public async Task ASectionThatIsNotChangedIsNotSent()
    {
        var (page, _, privacy, gateways, _) = await LoadedAsync();

        privacy.Emails = true;
        Assert.True(await page.SaveAsync());

        Assert.Null(gateways.Capture.Saved);
        Assert.Null(gateways.Updates.Chosen);
        Assert.True(gateways.Privacy.Saved!.Emails);
    }

    [Fact]
    public async Task ASectionTheServiceRefusesIsNamedAndKeepsItsChangesWhileTheOthersCommit()
    {
        var (page, capture, privacy, gateways, _) = await LoadedAsync();
        gateways.Privacy.Refusal = "The capture service refused the settings.";

        capture.GraceSeconds = 200;
        privacy.Telemetry = true;

        Assert.False(await page.SaveAsync());

        Assert.Equal(200, gateways.Capture.Saved!.GraceSeconds);
        Assert.Equal(SaveBarState.Failed, page.State);
        var failure = Assert.Single(page.Failures);
        Assert.Equal("Privacy", failure.Section);
        Assert.Equal("Privacy was not saved. The capture service refused the settings. The other changes were saved.", page.FailureMessage);

        // Capture committed; Privacy's change is still there, still pending.
        Assert.Equal(0, page.PendingIn("Capture"));
        Assert.Equal(1, page.PendingIn("Privacy"));
        Assert.True(privacy.Telemetry);

        // Once the service takes it, the bar goes.
        gateways.Privacy.Refusal = null;
        Assert.True(await page.SaveAsync());
        Assert.True(gateways.Privacy.Saved!.Telemetry);
        Assert.Equal(SaveBarState.Hidden, page.State);
        Assert.Null(page.FailureMessage);
    }

    [Fact]
    public async Task ASectionThatCannotBeSavedAsItIsIsNamedWithItsReasonAndTheOthersStillCommit()
    {
        var (page, capture, privacy, gateways, _) = await LoadedAsync();

        // All windows, not yet confirmed (INV-5): Capture holds its own reason.
        capture.CaptureAllWindows = true;
        privacy.Ssn = false;

        Assert.False(await page.SaveAsync());

        Assert.Null(gateways.Capture.Saved);
        Assert.False(gateways.Privacy.Saved!.Ssn);
        Assert.Equal("Capture was not saved. Confirm capturing all windows before saving. The other changes were saved.", page.FailureMessage);
        Assert.Equal(1, page.PendingIn("Capture"));
        Assert.Equal(0, page.PendingIn("Privacy"));
    }

    [Fact]
    public async Task AFailureThatIsNoLongerPendingIsNoLongerSaid()
    {
        var (page, capture, _, _, _) = await LoadedAsync();
        capture.CaptureAllWindows = true;
        Assert.False(await page.SaveAsync());
        Assert.Equal(SaveBarState.Failed, page.State);

        capture.CaptureAllWindows = false;

        Assert.Empty(page.Failures);
        Assert.Equal(SaveBarState.Hidden, page.State);
    }

    [Fact]
    public async Task WhenEverySectionFailsTheMessageDoesNotClaimAnythingWasSaved()
    {
        var (page, capture, _, gateways, _) = await LoadedAsync();
        gateways.Capture.Refusal = "Ctrl+Alt+R is taken by another app.";
        capture.AutoStart = false;

        Assert.False(await page.SaveAsync());

        Assert.Equal("Capture was not saved. Ctrl+Alt+R is taken by another app.", page.FailureMessage);
    }

    [Fact]
    public async Task UndoPutsEverySectionBackToWhatWasLastSaved()
    {
        var (page, capture, privacy, gateways, updates) = await LoadedAsync();

        capture.SetTool("rdp", false);
        capture.SetHotkey(Core.Input.HotkeyAction.MarkMoment, "Ctrl+Alt+K");
        capture.MicrophoneId = "{0.0.1.00000000}.{headset}";
        privacy.LocalOnly = true;
        privacy.NewPattern = @"ACME-\d{4}";
        Assert.True(privacy.AddPattern());
        updates.Choose(UpdateChannel.Pilot);
        Assert.True(page.HasPendingChanges);

        page.Undo();

        Assert.False(page.HasPendingChanges);
        Assert.Equal(SaveBarState.Hidden, page.State);
        Assert.True(capture.Tools.Single(t => t.Id == "rdp").Enabled);
        Assert.Equal(Core.Input.HotkeyBindings.Defaults[Core.Input.HotkeyAction.MarkMoment].ToString(), capture.HotkeyText(Core.Input.HotkeyAction.MarkMoment));
        Assert.Null(capture.MicrophoneId);
        Assert.False(privacy.LocalOnly);
        Assert.Empty(privacy.CustomPatterns);
        Assert.Equal(UpdateChannel.Stable, updates.Shown);

        // Undo asks the service nothing.
        Assert.Null(gateways.Capture.Saved);
        Assert.Null(gateways.Privacy.Saved);
        Assert.Equal(1, gateways.Capture.Loads);
        Assert.Equal(1, gateways.Privacy.Loads);
    }

    [Fact]
    public async Task UndoAfterASaveGoesBackToTheSavedValuesNotTheFirstLoad()
    {
        var (page, capture, _, _, _) = await LoadedAsync();
        capture.GraceSeconds = 60;
        Assert.True(await page.SaveAsync());

        capture.GraceSeconds = 240;
        page.Undo();

        Assert.Equal(60, capture.GraceSeconds);
        Assert.False(page.HasPendingChanges);
    }

    [Fact]
    public async Task UndoClearsAFailure()
    {
        var (page, _, privacy, gateways, _) = await LoadedAsync();
        gateways.Privacy.Refusal = "No.";
        privacy.Telemetry = true;
        Assert.False(await page.SaveAsync());

        page.Undo();

        Assert.Empty(page.Failures);
        Assert.Null(page.FailureMessage);
        Assert.Equal(SaveBarState.Hidden, page.State);
        Assert.False(privacy.Telemetry);
    }

    [Fact]
    public async Task FieldsThePolicyLockedAreNeverPendingAndStayAsThePolicyHasThem()
    {
        var gateways = new Gateways
        {
            Capture = { Snapshot = CaptureSnapshotWith(policyForcesAllWindows: true) },
            Privacy = { Snapshot = new SettingsSnapshot(new ClientSettings { LocalOnly = false, RetentionDays = 7 }, LocalOnlyLocked: true, RetentionLocked: true, PolicyVersion: "p7", EffectiveLocalOnly: true, EffectiveRetentionDays: 3) },
            Updates = { Snapshot = UpdateSnapshotWith(channelLocked: true) },
        };
        var (page, capture, privacy, _, updates) = await LoadedAsync(gateways: gateways);

        // The radio and the toggles are read-only on screen; a write that reaches the panels anyway
        // (a slider clamping, a radio group settling) is not a change.
        capture.CaptureAllWindows = false;
        privacy.LocalOnly = false;
        privacy.RetentionDays = 30;
        updates.Choose(UpdateChannel.Pilot);

        Assert.False(page.HasPendingChanges);
        Assert.True(privacy.LocalOnly);
        Assert.Equal(3, privacy.RetentionDays);
        Assert.Equal("Set by your admin (policy p7)", privacy.LockedBecause);
        Assert.Equal(UpdateChannel.Stable, updates.Shown);

        // A change elsewhere saves the technician's own values for the locked fields, untouched.
        privacy.Telemetry = true;
        Assert.True(await page.SaveAsync());
        Assert.False(gateways.Privacy.Saved!.LocalOnly);
        Assert.Equal(7, gateways.Privacy.Saved.RetentionDays);
        Assert.Null(gateways.Updates.Chosen);
    }

    [Fact]
    public async Task AnIntegrationBeingFilledInIsPendingAndSavedWithThePage()
    {
        var gateways = new Gateways();
        var integrations = new IntegrationsPanel(gateways.Integrations);
        await integrations.LoadAsync();
        var page = new SettingsPage([new IntegrationsSection(integrations)]);

        // A card opened with nothing typed is not a change.
        Assert.False(page.HasPendingChanges);

        integrations.Hudu.SiteUrl = "https://acme.huducloud.com";
        Assert.Equal(["Hudu"], page.Sections[0].Changes);
        integrations.Hudu.ApiKey = "abcd1234";

        Assert.True(await page.SaveAsync());

        Assert.Equal(("hudu", "https://acme.huducloud.com", "abcd1234"), gateways.Integrations.Stored);
        Assert.True(integrations.Hudu.Connected);
        Assert.False(page.HasPendingChanges);
    }

    [Fact]
    public async Task AnIntegrationThatIsNotFilledInIsNamedAndTheOtherCardStillSaves()
    {
        var gateways = new Gateways();
        var integrations = new IntegrationsPanel(gateways.Integrations);
        await integrations.LoadAsync();
        var page = new SettingsPage([new IntegrationsSection(integrations)]);

        integrations.ConnectWise.SiteUrl = "https://na.myconnectwise.net";
        integrations.Hudu.SiteUrl = "https://acme.huducloud.com";
        integrations.Hudu.ApiKey = "abcd1234";

        Assert.False(await page.SaveAsync());

        Assert.Equal("hudu", gateways.Integrations.Stored?.Provider);
        Assert.Equal(["ConnectWise Manage"], page.Sections[0].Changes);
        Assert.StartsWith("Integrations was not saved. ConnectWise Manage: The company ID", page.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndoOnAConnectedCardClosesItAndForgetsWhatWasTyped()
    {
        var gateways = new Gateways();
        gateways.Integrations.Rows = [new IntegrationDetail("hudu", "https://acme.huducloud.com", "••••1234", null, null, null)];
        var integrations = new IntegrationsPanel(gateways.Integrations);
        await integrations.LoadAsync();
        var page = new SettingsPage([new IntegrationsSection(integrations)]);

        integrations.Hudu.Change();
        Assert.False(page.HasPendingChanges);
        integrations.Hudu.ApiKey = "new-key";
        Assert.True(page.HasPendingChanges);

        page.Undo();

        Assert.False(integrations.Hudu.Editing);
        Assert.Equal(string.Empty, integrations.Hudu.ApiKey);
        Assert.Equal("https://acme.huducloud.com", integrations.Hudu.SiteUrl);
        Assert.False(page.HasPendingChanges);
        Assert.Null(gateways.Integrations.Stored);
    }

    [Fact]
    public async Task ASecondSaveWhileOneIsOnItsWaySendsNothing()
    {
        var (page, _, privacy, gateways, _) = await LoadedAsync();
        var held = new TaskCompletionSource<string?>();
        gateways.Privacy.Hold = held.Task;
        privacy.Telemetry = true;

        var first = page.SaveAsync();
        Assert.Equal(SaveBarState.Saving, page.State);
        Assert.False(page.CanSave);
        Assert.False(page.CanUndo);
        Assert.False(await page.SaveAsync());

        held.SetResult(null);
        Assert.True(await first);
        Assert.Equal(1, gateways.Privacy.Saves);
    }

    private static async Task<(SettingsPage Page, CapturePanel Capture, PrivacyPanel Privacy, Gateways Gateways, UpdatesSection Updates)> LoadedAsync(Action<bool>? startAtLogin = null, Gateways? gateways = null)
    {
        gateways ??= new Gateways();
        var capture = new CapturePanel(gateways.Capture);
        var privacy = new PrivacyPanel(gateways.Privacy);
        var updatesPanel = new UpdatesPanel(gateways.Updates);
        await capture.LoadAsync();
        await privacy.LoadAsync();
        await updatesPanel.LoadAsync();
        var updates = new UpdatesSection(updatesPanel);
        var page = new SettingsPage(
        [
            new CaptureSection(capture, on =>
            {
                startAtLogin?.Invoke(on);
                return Task.CompletedTask;
            }),
            new PrivacySection(privacy),
            updates,
        ]);
        return (page, capture, privacy, gateways, updates);
    }

    private static CaptureSnapshot CaptureSnapshotWith(bool policyForcesAllWindows = false) => new(
        new CaptureSettings(),
        [new ToolChoice("screenconnect", "ScreenConnect"), new ToolChoice("rdp", "Remote Desktop")],
        [new MicrophoneDevice("{0.0.1.00000000}.{headset}", "Headset", IsDefault: false), new MicrophoneDevice("{0.0.1.00000000}.{built-in}", "Built-in microphone", IsDefault: true)],
        policyForcesAllWindows,
        DefaultGraceSeconds: 120);

    private static UpdateSnapshot UpdateSnapshotWith(bool channelLocked = false) =>
        new("current", "0.6.0", null, UpdateChannel.Stable, channelLocked, "ScreenTail is up to date.", CanInstall: false);

    private sealed class Gateways
    {
        public FakeCapture Capture { get; } = new();

        public FakePrivacy Privacy { get; } = new();

        public FakeUpdates Updates { get; } = new();

        public FakeIntegrations Integrations { get; } = new();
    }

    private sealed class FakeCapture : ICaptureGateway
    {
        public CaptureSnapshot Snapshot { get; set; } = CaptureSnapshotWith();

        public CaptureSettings? Saved { get; private set; }

        public string? Refusal { get; set; }

        public int Loads { get; private set; }

        public Task<CaptureSnapshot?> LoadAsync(CancellationToken ct = default)
        {
            Loads++;
            return Task.FromResult<CaptureSnapshot?>(Snapshot);
        }

        public Task<string?> SaveAsync(CaptureSettings settings, CancellationToken ct = default)
        {
            if (Refusal is null)
            {
                Saved = settings;
            }

            return Task.FromResult(Refusal);
        }
    }

    private sealed class FakePrivacy : IPrivacyGateway
    {
        public SettingsSnapshot Snapshot { get; set; } = new(new ClientSettings(), LocalOnlyLocked: false, RetentionLocked: false, PolicyVersion: "default", EffectiveLocalOnly: false, EffectiveRetentionDays: 7);

        public ClientSettings? Saved { get; private set; }

        public string? Refusal { get; set; }

        public Task<string?>? Hold { get; set; }

        public int Loads { get; private set; }

        public int Saves { get; private set; }

        public Task<SettingsSnapshot?> LoadAsync(CancellationToken ct = default)
        {
            Loads++;
            return Task.FromResult<SettingsSnapshot?>(Snapshot);
        }

        public async Task<string?> SaveAsync(ClientSettings settings, CancellationToken ct = default)
        {
            Saves++;
            var refusal = Hold is null ? Refusal : await Hold.ConfigureAwait(false);
            if (refusal is null)
            {
                Saved = settings;
            }

            return refusal;
        }

        public Task<AuditSaved> ExportAuditAsync(string destination, CancellationToken ct = default) => throw new InvalidOperationException("not under test");
    }

    private sealed class FakeUpdates : IUpdateGateway
    {
        public UpdateSnapshot Snapshot { get; set; } = UpdateSnapshotWith();

        public UpdateChannel? Chosen { get; private set; }

        public Task<UpdateSnapshot?> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult<UpdateSnapshot?>(Chosen is { } chosen ? Snapshot with { Channel = chosen } : Snapshot);

        public Task<string?> SetChannelAsync(UpdateChannel channel, CancellationToken ct = default)
        {
            Chosen = channel;
            return Task.FromResult<string?>(null);
        }

        public Task<string?> InstallAsync(CancellationToken ct = default) => throw new InvalidOperationException("not under test");
    }

    private sealed class FakeIntegrations : IIntegrationsGateway
    {
        public IReadOnlyList<IntegrationDetail> Rows { get; set; } = [];

        public (string Provider, string Site, string Secret)? Stored { get; private set; }

        public Task<IReadOnlyList<IntegrationDetail>?> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IntegrationDetail>?>(Rows);

        public Task<string?> StoreAsync(string provider, string siteUrl, string secret, CancellationToken ct = default)
        {
            Stored = (provider, siteUrl, secret);
            return Task.FromResult<string?>(null);
        }

        public Task<string?> RemoveAsync(string provider, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<CheckOutcome> CheckAsync(string provider, CancellationToken ct = default) => Task.FromResult(new CheckOutcome(true, "ok"));

        public Task<CompanyMappingsPage?> MappingsAsync(CancellationToken ct = default) => Task.FromResult<CompanyMappingsPage?>(new CompanyMappingsPage([], []));

        public Task<string?> MapAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> UnmapAsync(string psaCompany, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
