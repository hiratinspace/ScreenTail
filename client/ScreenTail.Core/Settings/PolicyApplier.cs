using ScreenTail.Core.Capture;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Net;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Core.Settings;

/// <summary>
/// Where the tenant's policy and the technician's settings become enforcement (ST-046, ST-047, ST-080,
/// ST-081): local-only and its lock on the egress guard (INV-8, INV-11), retention on the purge job
/// (INV-12), exclusions and all-windows on the scope (INV-5, INV-6), the grace on the trigger, the
/// sensitivity on the scene sampler and the patterns on the redaction engine.
///
/// It used to be a local function in the service's composition root, so the one place those invariants
/// turn from a setting into behaviour could not be run by any test (2026-10-12 review). As plain Core the
/// Mac suite drives it from a policy fetch or a settings save down to a request the guard refuses.
///
/// <b>Three inputs, each kept in memory and replaced whole.</b> A new policy is read from the sync; a saved
/// settings document or saved capture settings arrive as what the command just wrote. Nothing is read back
/// from disk here: a settings file that a scanner happens to be holding reads back as the defaults, and an
/// apply that took those would quietly lift a local-only the technician had switched on.
///
/// <b>One apply at a time.</b> A fetch, a Settings save and a Capture save arrive on different threads.
/// Each apply takes the lock, records its own input and resolves all three as they are now, so whichever
/// finishes last is built from the newest of everything. Without it the slower of two could finish with
/// the other's input from before it changed, and undo a local-only that had just been turned on.
/// </summary>
public sealed class PolicyApplier
{
    private readonly Lock _gate = new();
    private readonly PolicySync _policy;
    private readonly EgressPolicy _egress;
    private readonly RetentionOptions _retention;
    private readonly ScopePolicy _scope;
    private readonly SessionTrigger _trigger;
    private readonly SceneSampler _sampler;
    private readonly RedactionEngine _redaction;
    private readonly ExclusionList? _exclusions;
    private ClientSettings _settings;
    private CaptureSettings _capture;
    private AppliedPolicy? _last;

    /// <param name="policy">The sync whose <see cref="PolicySync.Current"/> is the policy in force. Every fetch that brings a different one applies it.</param>
    /// <param name="settings">The technician's settings as they are on disk now.</param>
    /// <param name="capture">The capture settings as they are on disk now.</param>
    /// <param name="scope">
    /// Built with the shipped exclusions. They are kept through every apply: a list that a settings save
    /// could drop would stop excluding password managers until the next restart (INV-6).
    /// </param>
    public PolicyApplier(
        PolicySync policy,
        ClientSettings settings,
        CaptureSettings capture,
        EgressPolicy egress,
        RetentionOptions retention,
        ScopePolicy scope,
        SessionTrigger trigger,
        SceneSampler sampler,
        RedactionEngine redaction)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _egress = egress ?? throw new ArgumentNullException(nameof(egress));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _redaction = redaction ?? throw new ArgumentNullException(nameof(redaction));
        _exclusions = scope.Options.Exclusions;

        // The argument is ignored on purpose: Current is already the new policy when this is raised, and
        // reading it under the lock is what keeps two applies in order.
        _policy.Changed += _ => Apply();
    }

    /// <summary>
    /// Raised after every apply, under the lock, with what is now in force. The host logs it; it is never
    /// content (INV-10). A handler must be quick and must not wait on another thread's apply.
    /// </summary>
    public event Action<AppliedPolicy>? Applied;

    /// <summary>
    /// The local-only flag and policy version a session starting now records (ST-047), both from the last
    /// apply, one record read whole. Read from the guard and the sync apart, a start during a policy change
    /// could pair the new version with the old flag.
    ///
    /// Before the first apply the flag is on: the host applies before it opens the pipe or starts the
    /// coordinator, so nothing should ask sooner, and if something does the closed answer is the one given.
    /// </summary>
    public (bool LocalOnly, string PolicyVersion) ForNewSession() =>
        Volatile.Read(ref _last) is { } last ? (last.LocalOnly, last.Version) : (true, _policy.Current.Version);

    /// <summary>Applies the policy in force and the settings last handed over. The host calls it once at start.</summary>
    public AppliedPolicy Apply() => Apply(null, null);

    /// <summary>The technician saved Settings → Privacy (ST-081): applied in place, under the admin's policy.</summary>
    public AppliedPolicy Apply(ClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Apply(settings, null);
    }

    /// <summary>The technician saved Settings → Capture (ST-080): applied in place, under the admin's policy.</summary>
    public AppliedPolicy Apply(CaptureSettings capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return Apply(null, capture);
    }

    private AppliedPolicy Apply(ClientSettings? settings, CaptureSettings? capture)
    {
        AppliedPolicy applied;
        lock (_gate)
        {
            _settings = settings ?? _settings;
            _capture = capture ?? _capture;
            var current = _policy.Current;
            applied = PolicyApplication.Resolve(current, _settings);
            Volatile.Write(ref _last, applied);

            // The guard first: of everything here it is the one a request on another thread is waiting on.
            // The update checks' exception is the admin's, to a local-only the admin locked on (ST-113, INV-8).
            // A local-only the technician turned on themselves lets nothing out, whatever the policy allows.
            _egress.Apply(applied.LocalOnly, applied.Enforced, applied.Enforced && current.UpdateChecksUnderLocalOnly);
            _retention.Retention = applied.Retention;
            _scope.Apply(new ScopeOptions
            {
                Exclusions = _exclusions,

                // The admin's scope wins where set; otherwise the technician's (ST-080), confirmed in Settings.
                CaptureAllWindows = applied.CaptureAllWindows || _capture.CaptureAllWindows,
                ExcludedProcesses = new HashSet<string>(_settings.ExcludedProcesses, StringComparer.OrdinalIgnoreCase),
                DisabledTools = new HashSet<string>(_capture.DisabledTools, StringComparer.OrdinalIgnoreCase),
            });
            _trigger.Apply(_capture.GraceSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : _scope.Registry.Grace);
            _sampler.Apply(_capture.ToSceneOptions());
            _redaction.Apply(_settings.ToRedactionPolicy());

            // Inside the lock, so reports come out in the order the applies took effect and the last line
            // the host logs is what the guard enforces. The one subscriber writes a log line.
            Applied?.Invoke(applied);
        }

        return applied;
    }
}
