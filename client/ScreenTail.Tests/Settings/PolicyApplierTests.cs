using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenTail.Core.Audit;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;
using ScreenTail.Core.Net;
using ScreenTail.Core.Outbox;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Settings;
using ScreenTail.Core.Speech;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// Where the tenant's policy and the technician's settings become enforcement (ST-046, ST-047, ST-080,
/// ST-081). Until the 2026-10-12 review this was a local function in the service's composition root and
/// no test ran it: the policy tests set the guard, the retention and the scope by hand and then read back
/// what they had set, so passing the technician's local-only where the admin's locked one belonged stayed
/// green. These start where the service starts, a policy fetch or a settings save, and end where it ends,
/// a request that does or does not reach the far side of the egress guard.
/// </summary>
public sealed class PolicyApplierTests : IAsyncDisposable
{
    private static readonly Uri Address = new("https://api.screentail.example/");
    private static readonly DateTimeOffset At = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));
    private SqliteSessionStore? _store;

    [Fact]
    public async Task SavingLocalOnlyMakesTheEgressGuardRefuseTheBackend()
    {
        using var rig = new Rig();
        _ = rig.Applier.Apply();
        var commands = new SettingsCommands(
            new ClientSettingsStore(Path.Combine(_dir, "settings.json")),
            () => rig.Sync.Current,
            settings => rig.Applier.Apply(settings),
            _ => Task.FromResult<(IReadOnlyList<AuditRecord>, AuditVerification)>(([], new AuditVerification(true, 0, 0, null))));
        var metrics = new MetricsSender(rig.Http, () => "a-device-token");

        var before = await metrics.SendAsync(Item(OutboxKind.Metric), TestContext.Current.CancellationToken);
        var saved = await commands.HandleAsync(new SetSettingsCommand { RequestId = 1, Settings = new ClientSettings { LocalOnly = true } }, TestContext.Current.CancellationToken);
        var after = await metrics.SendAsync(Item(OutboxKind.Metric), TestContext.Current.CancellationToken);

        Assert.Equal(OutboxState.Done, before.State);
        Assert.True(saved!.Ok, saved.Error);
        Assert.Equal(OutboxState.Failed, after.State);
        Assert.Contains("Local-only", after.Message, StringComparison.Ordinal);
        Assert.Equal(1, rig.Backend.Asked("/v1/metrics/sessions"));
        Assert.Equal("Local-only", rig.Egress.Badge);
        Assert.True(rig.Egress.CanChangeLocally);
    }

    [Fact]
    public async Task ALockedLocalOnlyFromTheBackendStopsDraftingWithoutARestart()
    {
        using var rig = new Rig(new ClientSettings { LocalOnly = false });
        _ = rig.Applier.Apply();
        var drafts = new DraftSender(rig.Http, await FinishedSessionAsync(), () => "a-device-token");

        var before = await drafts.SendAsync(Item(OutboxKind.Draft), TestContext.Current.CancellationToken);
        rig.Backend.Policy = new TenantPolicy("p2", 7, LocalOnly: true, LocalOnlyLocked: true, CaptureAllWindows: false);
        Assert.True(await rig.Sync.FetchAsync(TestContext.Current.CancellationToken));
        var after = await drafts.SendAsync(Item(OutboxKind.Draft), TestContext.Current.CancellationToken);

        // The draft before the policy reached the backend (it answered "try later"); the one after never left.
        Assert.Equal(OutboxState.Pending, before.State);
        Assert.Equal(OutboxState.Failed, after.State);
        Assert.Contains("Local-only", after.Message, StringComparison.Ordinal);
        Assert.Equal(1, rig.Backend.Asked("/v1/sessions/summarize"));
        Assert.False(rig.Egress.CanChangeLocally);
        Assert.Equal("Local-only (set by your administrator)", rig.Egress.Badge);

        // INV-11: the technician saving local-only off does not lift a lock the admin set.
        _ = rig.Applier.Apply(new ClientSettings { LocalOnly = false });
        var saved = await drafts.SendAsync(Item(OutboxKind.Draft), TestContext.Current.CancellationToken);
        Assert.Equal(OutboxState.Failed, saved.State);
        Assert.Equal(1, rig.Backend.Asked("/v1/sessions/summarize"));
    }

    [Fact]
    public async Task WhenTheAdminLiftsTheLockTheTechniciansOwnChoiceComesBack()
    {
        // Both directions, without a restart: the policy that locked local-only on is lifted by the next one,
        // and then what the technician chose is what holds.
        using var rig = new Rig(new ClientSettings { LocalOnly = false });
        rig.Backend.Policy = new TenantPolicy("p2", 7, LocalOnly: true, LocalOnlyLocked: true, CaptureAllWindows: false);
        Assert.True(await rig.Sync.FetchAsync(TestContext.Current.CancellationToken));
        Assert.True(rig.Egress.Settings.LocalOnly);

        rig.Backend.Policy = new TenantPolicy("p3", 7, LocalOnly: true, LocalOnlyLocked: false, CaptureAllWindows: false);
        Assert.True(await rig.Sync.FetchAsync(TestContext.Current.CancellationToken));

        Assert.False(rig.Egress.Settings.LocalOnly);
        Assert.True(rig.Egress.CanChangeLocally);
        Assert.True(rig.Egress.Decide(EgressPurpose.Summarisation, new Uri(Address, "v1/sessions/summarize")).Allowed);
    }

    [Fact]
    public async Task ANewPolicyThatDoesNotLockLocalOnlyKeepsTheTechniciansChoice()
    {
        // The policy event must apply the settings saved since, not the ones the service started with.
        using var rig = new Rig(new ClientSettings { LocalOnly = false });
        _ = rig.Applier.Apply();
        _ = rig.Applier.Apply(new ClientSettings { LocalOnly = true });

        rig.Backend.Policy = new TenantPolicy("p2", 3, LocalOnly: false, LocalOnlyLocked: false, CaptureAllWindows: false);
        Assert.True(await rig.Sync.FetchAsync(TestContext.Current.CancellationToken));

        Assert.True(rig.Egress.Settings.LocalOnly);
        Assert.False(rig.Egress.Decide(EgressPurpose.Backend, new Uri(Address, "v1/metrics/sessions")).Allowed);
        Assert.Equal(TimeSpan.FromDays(3), rig.Retention.Retention);
    }

    [Fact]
    public async Task ACaptureSaveKeepsTheSettingsSavedBeforeIt()
    {
        // Nothing is read back from disk: the rig has no settings file at all, and a capture save that
        // reloaded one would come back with the defaults and local-only off.
        using var rig = new Rig();
        _ = rig.Applier.Apply(new ClientSettings { LocalOnly = true, RetentionDays = 2, ExcludedProcesses = ["notepad"] });

        _ = rig.Applier.Apply(new CaptureSettings { Sensitivity = "high" });
        rig.Backend.Policy = new TenantPolicy("default", 7, false, false, false);
        _ = await rig.Sync.FetchAsync(TestContext.Current.CancellationToken);

        Assert.True(rig.Egress.Settings.LocalOnly);
        Assert.Equal(TimeSpan.FromDays(2), rig.Retention.Retention);
        Assert.Contains("notepad", rig.Scope.Options.ExcludedProcesses);
        Assert.Equal(5, rig.Sampler.Options.Threshold);
    }

    [Fact]
    public async Task TheAdminsRetentionReplacesTheTechniciansWhileAPolicyIsInForce()
    {
        using var rig = new Rig(new ClientSettings { RetentionDays = 14 });

        _ = rig.Applier.Apply();
        Assert.Equal(TimeSpan.FromDays(14), rig.Retention.Retention);

        rig.Backend.Policy = new TenantPolicy("p2", 3, false, false, false);
        Assert.True(await rig.Sync.FetchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromDays(3), rig.Retention.Retention);

        // A save does not take it back while the policy stands (INV-11, INV-12).
        _ = rig.Applier.Apply(new ClientSettings { RetentionDays = 30 });
        Assert.Equal(TimeSpan.FromDays(3), rig.Retention.Retention);
    }

    [Fact]
    public async Task TheShippedExclusionsSurviveEverySaveAndAllWindowsIsTheAdminsOrTheTechniciansChoice()
    {
        using var rig = new Rig();
        var shipped = rig.Scope.Options.Exclusions;

        _ = rig.Applier.Apply(new ClientSettings { ExcludedProcesses = ["notepad"] });
        _ = rig.Applier.Apply(new CaptureSettings { CaptureAllWindows = true, DisabledTools = ["rdp"] });

        Assert.Same(shipped, rig.Scope.Options.Exclusions);
        Assert.True(rig.Scope.Options.CaptureAllWindows);
        Assert.Equal(CaptureScope.Excluded, rig.Scope.Decide(Window("KeePass")).Scope);
        Assert.Equal(CaptureScope.Excluded, rig.Scope.Decide(Window("notepad")).Scope);
        Assert.Equal(CaptureScope.AdminTool, rig.Scope.Decide(Window("calc")).Scope);
        Assert.Equal(CaptureScope.OutOfScope, rig.Scope.Decide(Window("mstsc")).Scope);

        // The technician turns all-windows off; the admin's policy turns it back on, and only the admin's
        // next policy turns it off again.
        _ = rig.Applier.Apply(new CaptureSettings { CaptureAllWindows = false });
        Assert.False(rig.Scope.Options.CaptureAllWindows);
        rig.Backend.Policy = new TenantPolicy("p2", 7, false, false, CaptureAllWindows: true);
        Assert.True(await rig.Sync.FetchAsync(TestContext.Current.CancellationToken));
        Assert.True(rig.Scope.Options.CaptureAllWindows);
        Assert.Same(shipped, rig.Scope.Options.Exclusions);
        Assert.Equal(CaptureScope.Excluded, rig.Scope.Decide(Window("KeePass")).Scope);
    }

    [Fact]
    public void CaptureSettingsReachTheTriggerAndTheSampler()
    {
        using var rig = new Rig();

        _ = rig.Applier.Apply(new CaptureSettings { GraceSeconds = 120, Sensitivity = "high" });
        Assert.Equal(TimeSpan.FromSeconds(120), rig.Trigger.Grace);
        Assert.Equal(5, rig.Sampler.Options.Threshold);

        _ = rig.Applier.Apply(new CaptureSettings());
        Assert.Equal(rig.Scope.Registry.Grace, rig.Trigger.Grace);
        Assert.Equal(new SceneSamplerOptions().Threshold, rig.Sampler.Options.Threshold);
    }

    [Fact]
    public void SavedRedactionChoicesReachTheEngine()
    {
        using var rig = new Rig();
        const string Said = "send it to someone@example.com please";

        _ = rig.Applier.Apply(new ClientSettings { Emails = true });
        Assert.True(rig.Redaction.ScrubText(Said).Changed);

        _ = rig.Applier.Apply(new ClientSettings { Emails = false });
        Assert.False(rig.Redaction.ScrubText(Said).Changed);
    }

    [Fact]
    public void EveryApplyReportsWhatIsNowInForce()
    {
        using var rig = new Rig();
        var reported = new List<AppliedPolicy>();
        rig.Applier.Applied += reported.Add;

        var first = rig.Applier.Apply();
        var second = rig.Applier.Apply(new ClientSettings { LocalOnly = true });

        Assert.Equal([first, second], reported);
        Assert.Equal("default", second.Version);
        Assert.True(second.LocalOnly);
        Assert.False(second.Enforced);
    }

    [Fact]
    public async Task SavesOnDifferentThreadsEndWithTheNewestOfEverything()
    {
        // A Settings save and a Capture save racing. Whatever the interleaving, the last settings saved
        // switched local-only on, so that is what the guard must end with. A race test cannot prove the
        // lock is there; without it this fails often enough to be noticed.
        for (var round = 0; round < 50; round++)
        {
            using var rig = new Rig();
            using var start = new Barrier(2);
            var settings = Task.Run(
                () =>
                {
                    start.SignalAndWait(TestContext.Current.CancellationToken);
                    for (var i = 0; i < 2000; i++)
                    {
                        _ = rig.Applier.Apply(new ClientSettings { LocalOnly = i % 2 == 1 });
                    }
                },
                TestContext.Current.CancellationToken);
            var capture = Task.Run(
                () =>
                {
                    start.SignalAndWait(TestContext.Current.CancellationToken);
                    for (var i = 0; i < 2000; i++)
                    {
                        _ = rig.Applier.Apply(new CaptureSettings { Sensitivity = i % 2 == 0 ? "low" : "high" });
                    }
                },
                TestContext.Current.CancellationToken);

            await Task.WhenAll(settings, capture);

            Assert.True(rig.Egress.Settings.LocalOnly, $"round {round}");
            Assert.Equal(5, rig.Sampler.Options.Threshold);
        }
    }

    private static OutboxItem Item(OutboxKind kind) => new(
        "o1", "s1", kind, $"{kind}:s1", "{}", OutboxState.Pending, 0, At, At, null, null, null);

    private static ForegroundWindowInfo Window(string process) => new(1, 100, process, "Untitled", "Window", null, false, At);

    /// <summary>A finished session with one redacted frame, so a draft has something real to send.</summary>
    private async Task<SqliteSessionStore> FinishedSessionAsync()
    {
        var store = _store ??= await SqliteSessionStore.OpenAsync(Path.Combine(_dir, "store.db"), new FixedKey(RandomNumberGenerator.GetBytes(32)));
        await store.CreateSessionAsync(new NewSession("s1", At, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await store.StageFrameAsync("s1", new StagedFrame("f1", 1_000, FrameTrigger.Click, 1920, 1080, null, new byte[] { 0x01 }));
        await store.MarkFrameRedactedAsync("f1", new RedactionOutcome(new byte[] { 0xFF, 0xD8, 0xFF }, "Services", [], SensitiveContext: false, At));
        await store.FinalizeSessionAsync("s1", new FinalizeInfo(60_000, false));
        return store;
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// The pieces the service builds, built the same way: one guarded client for drafts, metrics and the
    /// policy fetch, the deployment's egress shape, the shipped registry and a small exclusion list.
    /// </summary>
    private sealed class Rig : IDisposable
    {
        public Rig(ClientSettings? settings = null, CaptureSettings? capture = null)
        {
            Egress = new EgressPolicy(EgressSettings.ForBackend(Address, SpeechModels.Hosts));
            Http = new HttpClient(new EgressGuard(Egress, Backend)) { BaseAddress = Address };
            Sync = new PolicySync(Http, () => "a-device-token", new MemoryCache());
            var registry = RemoteToolRegistry.Load(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));
            Scope = new ScopePolicy(registry, new ScopeOptions { Exclusions = ExclusionList.Load(Exclusions) });
            Trigger = new SessionTrigger(Scope);
            Applier = new PolicyApplier(Sync, settings ?? new ClientSettings(), capture ?? new CaptureSettings(), Egress, Retention, Scope, Trigger, Sampler, Redaction);
        }

        public ScriptedBackend Backend { get; } = new();

        public EgressPolicy Egress { get; }

        public HttpClient Http { get; }

        public PolicySync Sync { get; }

        public RetentionOptions Retention { get; } = new();

        public ScopePolicy Scope { get; }

        public SessionTrigger Trigger { get; }

        public SceneSampler Sampler { get; } = new();

        public RedactionEngine Redaction { get; } = new();

        public PolicyApplier Applier { get; }

        public void Dispose() => Http.Dispose();
    }

    private const string Exclusions = """
        { "version": "test", "processes": [ { "id": "keepass", "display_name": "KeePass", "processes": ["KeePass"] } ] }
        """;

    /// <summary>The backend: the policy it is scripted with, 204 for metrics, "try later" for drafts. Counts by path.</summary>
    private sealed class ScriptedBackend : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly ConcurrentDictionary<string, int> _asked = new(StringComparer.Ordinal);

        public TenantPolicy? Policy { get; set; }

        public int Asked(string path) => _asked.TryGetValue(path, out var count) ? count : 0;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            _ = _asked.AddOrUpdate(path, 1, (_, count) => count + 1);
            return Task.FromResult(path switch
            {
                "/v1/policy" when Policy is { } policy => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(policy, options: Json) },
                "/v1/metrics/sessions" => new HttpResponseMessage(HttpStatusCode.NoContent),
                "/v1/sessions/summarize" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }
    }

    private sealed class MemoryCache : IPolicyCache
    {
        private TenantPolicy? _saved;

        public TenantPolicy? Load() => _saved;

        public void Save(TenantPolicy policy) => _saved = policy;
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}
