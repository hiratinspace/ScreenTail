using System.Reflection;
using System.Text.Json.Serialization;
using ScreenTail.Core.Net;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Core.Settings;

/// <param name="LocalOnlyLocked">The admin's policy set local-only; the field is read-only here (INV-11).</param>
/// <param name="RetentionLocked">The admin's policy set retention.</param>
public sealed record SettingsSnapshot(ClientSettings Settings, bool LocalOnlyLocked, bool RetentionLocked, string PolicyVersion);

/// <summary>What Settings → Privacy asks the service (ST-081). Over the pipe in the running application; a fake in tests.</summary>
public interface IPrivacyGateway
{
    Task<SettingsSnapshot?> LoadAsync(CancellationToken ct = default);

    /// <summary>Null when saved and applied; otherwise the reason.</summary>
    Task<string?> SaveAsync(ClientSettings settings, CancellationToken ct = default);

    /// <summary>The audit log as JSON with its verification, or null when the service did not answer.</summary>
    Task<string?> ExportAuditAsync(CancellationToken ct = default);
}

/// <summary>
/// Settings → Privacy &amp; Redaction's rules (ST-081, Spec §5 S6). Every field is the technician's own
/// unless the admin's policy locked it, in which case it is shown as the policy has it and never sent
/// back changed. A custom pattern is checked as it is typed and tried against a sample; an invalid one
/// anywhere in the list blocks saving and says why (AC1). The telemetry list is read off the metric's
/// wire type, so the screen cannot promise less than is sent.
/// </summary>
public sealed class PrivacyPanel(IPrivacyGateway gateway)
{
    private readonly IPrivacyGateway _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    private readonly List<string> _patterns = [];
    private readonly List<string> _processes = [];
    private ClientSettings _loaded = new();
    private string? _refusal;

    /// <summary>The exact field names usage telemetry sends: <c>shared/contracts/session-metric.v1.json</c>, read off the type.</summary>
    public static IReadOnlyList<string> TelemetryFields { get; } =
    [
        .. typeof(SessionMetricWire).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name),
    ];

    public bool Loaded { get; private set; }

    public bool LocalOnly { get; set; }

    public bool LocalOnlyLocked { get; private set; }

    public int RetentionDays { get; set; } = 7;

    public bool RetentionLocked { get; private set; }

    public string PolicyVersion { get; private set; } = "default";

    public string LockedBecause => $"Set by your admin (policy {PolicyVersion})";

    public bool Telemetry { get; set; }

    public bool Ssn { get; set; } = true;

    public bool Cards { get; set; } = true;

    public bool ApiKeys { get; set; } = true;

    public bool Passwords { get; set; } = true;

    public bool Emails { get; set; }

    public IReadOnlyList<string> CustomPatterns => _patterns;

    public IReadOnlyList<string> ExcludedProcesses => _processes;

    public string NewPattern
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    public string? NewPatternProblem => RedactionPolicy.ValidateCustomPattern(NewPattern);

    public bool CanAddPattern => NewPatternProblem is null && !_patterns.Contains(NewPattern.Trim(), StringComparer.Ordinal);

    /// <summary>Text to try the new pattern against, and what it would become.</summary>
    public string Sample
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    public string SampleMasked => NewPatternProblem is null && Sample.Length > 0
        ? new RedactionEngine(new RedactionPolicy { Ssn = false, Cards = false, ApiKeys = false, Passwords = false, CustomPatterns = [NewPattern.Trim()] }).ScrubText(Sample).Text
        : Sample;

    public string NewProcess
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    public string? NewProcessProblem => ClientSettings.ProcessNameProblem(NewProcess);

    public bool CanAddProcess => NewProcessProblem is null && !_processes.Contains(NewProcess.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Why the settings cannot be saved as they are — the first problem, or the service's refusal — or null.</summary>
    public string? Problem
    {
        get
        {
            if (_refusal is not null)
            {
                return _refusal;
            }

            var problems = Current().Problems();
            return problems.Count > 0 ? problems[0] : null;
        }
    }

    public bool CanSave => Loaded && Problem is null;

    /// <summary>What the technician is about to save: the fields, with the locked ones as the policy has them.</summary>
    public ClientSettings Current() => new()
    {
        LocalOnly = LocalOnlyLocked ? _loaded.LocalOnly : LocalOnly,
        RetentionDays = RetentionLocked ? _loaded.RetentionDays : RetentionDays,
        Telemetry = Telemetry,
        Ssn = Ssn,
        Cards = Cards,
        ApiKeys = ApiKeys,
        Passwords = Passwords,
        Emails = Emails,
        CustomPatterns = [.. _patterns],
        ExcludedProcesses = [.. _processes],
    };

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _refusal = null;
        var snapshot = await _gateway.LoadAsync(ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            Loaded = false;
            _refusal = "The capture service did not answer, so these settings cannot be shown.";
            return;
        }

        _loaded = snapshot.Settings;
        LocalOnly = _loaded.LocalOnly;
        LocalOnlyLocked = snapshot.LocalOnlyLocked;
        RetentionDays = _loaded.RetentionDays;
        RetentionLocked = snapshot.RetentionLocked;
        PolicyVersion = snapshot.PolicyVersion;
        Telemetry = _loaded.Telemetry;
        Ssn = _loaded.Ssn;
        Cards = _loaded.Cards;
        ApiKeys = _loaded.ApiKeys;
        Passwords = _loaded.Passwords;
        Emails = _loaded.Emails;
        _patterns.Clear();
        _patterns.AddRange(_loaded.CustomPatterns);
        _processes.Clear();
        _processes.AddRange(_loaded.ExcludedProcesses);
        Loaded = true;
    }

    public bool AddPattern()
    {
        if (!CanAddPattern)
        {
            return false;
        }

        _patterns.Add(NewPattern.Trim());
        NewPattern = string.Empty;
        return true;
    }

    public void RemovePattern(string pattern) => _patterns.Remove(pattern);

    public bool AddProcess()
    {
        if (!CanAddProcess)
        {
            return false;
        }

        _processes.Add(NewProcess.Trim());
        NewProcess = string.Empty;
        return true;
    }

    public void RemoveProcess(string process) => _processes.RemoveAll(p => string.Equals(p, process, StringComparison.OrdinalIgnoreCase));

    /// <summary>Saves and applies. False leaves <see cref="Problem"/> saying why.</summary>
    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        _refusal = null;
        if (!CanSave)
        {
            return false;
        }

        var settings = Current();
        _refusal = await _gateway.SaveAsync(settings, ct).ConfigureAwait(false);
        if (_refusal is not null)
        {
            return false;
        }

        _loaded = settings;
        return true;
    }

    public Task<string?> ExportAuditAsync(CancellationToken ct = default) => _gateway.ExportAuditAsync(ct);
}

/// <summary>Settings → Privacy's gateway, over the pipe (ST-081).</summary>
public sealed class PipePrivacy(CaptureConnection connection) : IPrivacyGateway
{
    private readonly CaptureConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<SettingsSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        var reported = await _connection.RequestAsync<SettingsReported>(id => new GetSettingsCommand { RequestId = id }, ct).ConfigureAwait(false);
        return reported is null ? null : new SettingsSnapshot(reported.Settings, reported.LocalOnlyLocked, reported.RetentionLocked, reported.PolicyVersion);
    }

    public async Task<string?> SaveAsync(ClientSettings settings, CancellationToken ct = default)
    {
        var result = await _connection.SendAsync(id => new SetSettingsCommand { RequestId = id, Settings = settings }, ct).ConfigureAwait(false);
        return result.Ok ? null : result.Error ?? "The capture service refused the settings.";
    }

    public async Task<string?> ExportAuditAsync(CancellationToken ct = default)
    {
        var exported = await _connection.RequestAsync<AuditExported>(id => new ExportAuditCommand { RequestId = id }, ct).ConfigureAwait(false);
        return exported?.Json;
    }
}
