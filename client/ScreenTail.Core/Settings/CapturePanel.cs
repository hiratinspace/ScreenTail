using ScreenTail.Core.Input;
using ScreenTail.Core.Shell;
using ScreenTail.Core.Speech;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Core.Settings;

public sealed record ToolChoice(string Id, string DisplayName);

/// <param name="Hint">Size and accuracy in a phrase, for the model picker (Spec §5 S5).</param>
public sealed record ModelChoice(string Name, string Hint);

/// <param name="PolicyForcesAllWindows">The admin's policy set the scope; the radio is read-only here (INV-11).</param>
/// <param name="DefaultGraceSeconds">The registry's grace, shown when the technician has not set one.</param>
public sealed record CaptureSnapshot(CaptureSettings Settings, IReadOnlyList<ToolChoice> Tools, IReadOnlyList<string> Microphones, bool PolicyForcesAllWindows, int DefaultGraceSeconds);

/// <summary>What Settings → Capture asks the service (ST-080). Over the pipe in the running application; a fake in tests.</summary>
public interface ICaptureGateway
{
    Task<CaptureSnapshot?> LoadAsync(CancellationToken ct = default);

    /// <summary>Null when saved and applied; otherwise the reason.</summary>
    Task<string?> SaveAsync(CaptureSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Settings → Capture's rules (ST-080, Spec §5 S5). "All windows" warns about other customers' data
/// and is not saved until confirmed (INV-5); a hotkey is checked as typed and a clash names an
/// alternative inline (ST-029); the microphone and the speech model are chosen here and apply at the
/// next start, which the screen says rather than pretending otherwise.
/// </summary>
public sealed class CapturePanel(ICaptureGateway gateway)
{
    public const string AllWindowsWarning = "All windows means every window that has focus during a session is captured — a browser tab with other customers' data, your email, a password manager that is not excluded. Confirm you want this.";

    private readonly ICaptureGateway _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    private readonly List<ToolToggle> _tools = [];
    private readonly Dictionary<HotkeyAction, string> _hotkeys = [];
    private CaptureSettings _loaded = new();
    private bool _allWindowsConfirmed;
    private string? _refusal;

    public sealed class ToolToggle(ToolChoice tool, bool enabled)
    {
        public string Id => tool.Id;

        public string DisplayName => tool.DisplayName;

        public bool Enabled { get; internal set; } = enabled;
    }

    public bool Loaded { get; private set; }

    public IReadOnlyList<ToolToggle> Tools => _tools;

    public bool CaptureAllWindows
    {
        get;
        set
        {
            if (field != value)
            {
                _allWindowsConfirmed = false;
            }

            field = value;
        }
    }

    public bool ScopeLocked { get; private set; }

    /// <summary>The inline warning while "All windows" is chosen and not yet confirmed; null otherwise.</summary>
    public string? ScopeWarning => CaptureAllWindows && !ScopeLocked ? AllWindowsWarning : null;

    public bool AutoStart { get; set; } = true;

    public int GraceSeconds { get; set; } = 120;

    public int DefaultGraceSeconds { get; private set; } = 120;

    public string Sensitivity { get; set; } = "medium";

    /// <summary>The capture device(s) the service found, by name, for display: the recorder this build uses takes Windows' default communications device, so there is nothing to choose yet.</summary>
    public IReadOnlyList<string> Microphones { get; private set; } = [];

    public IReadOnlyList<ModelChoice> Models { get; } =
    [
        .. SpeechModels.All.Select(m => new ModelChoice(m.Name, m.Name switch
        {
            "tiny.en" => "smallest download, fastest, least accurate",
            "small.en" => "largest download, slowest, best accuracy",
            _ => "the middle: what most laptops run well",
        })),
    ];

    /// <summary>Null is the model chosen for this machine's cores.</summary>
    public string? Model { get; set; }

    public bool StartUiAtLogin { get; set; }

    public string AppliesAtNextStart { get; } = "The speech model applies at the next start of the capture service.";

    public string HotkeyText(HotkeyAction action) => _hotkeys.TryGetValue(action, out var text) ? text : HotkeyBindings.Defaults[action].ToString();

    public void SetHotkey(HotkeyAction action, string? text)
    {
        _refusal = null;
        _hotkeys[action] = (text ?? string.Empty).Trim();
    }

    /// <summary>Why this action's chord cannot be saved — a shape problem, or a clash with the alternative to try — or null.</summary>
    public string? HotkeyProblem(HotkeyAction action)
    {
        if (!_hotkeys.TryGetValue(action, out var text))
        {
            return null;
        }

        if (!Hotkey.TryParse(text, out var hotkey) || !hotkey.Value.IsUsable)
        {
            return "Not a chord like Ctrl+Alt+R: two modifiers and a key.";
        }

        var conflict = Current().Conflicts().FirstOrDefault(c => c.Action == action);
        return conflict is null ? null : $"{conflict.Reason}{(conflict.Suggestion is { } s ? $" Try {s}." : string.Empty)}";
    }

    public string? Problem
    {
        get
        {
            if (_refusal is not null)
            {
                return _refusal;
            }

            if (ScopeWarning is not null && !_allWindowsConfirmed)
            {
                return "Confirm capturing all windows before saving.";
            }

            var problems = Current().Problems();
            return problems.Count > 0 ? problems[0] : null;
        }
    }

    public bool CanSave => Loaded && Problem is null;

    public void SetTool(string id, bool enabled)
    {
        _refusal = null;
        foreach (var tool in _tools.Where(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            tool.Enabled = enabled;
        }
    }

    /// <summary>The technician read the warning and still wants every window (INV-5).</summary>
    public void ConfirmAllWindows() => _allWindowsConfirmed = true;

    public CaptureSettings Current() => new()
    {
        DisabledTools = [.. _tools.Where(t => !t.Enabled).Select(t => t.Id)],
        CaptureAllWindows = ScopeLocked ? _loaded.CaptureAllWindows : CaptureAllWindows,
        AutoStart = AutoStart,
        GraceSeconds = GraceSeconds == DefaultGraceSeconds ? null : GraceSeconds,
        Hotkeys = _hotkeys.Where(h => h.Value != HotkeyBindings.Defaults[h.Key].ToString()).ToDictionary(h => h.Key.ToString(), h => h.Value, StringComparer.Ordinal),
        Sensitivity = Sensitivity,
        SpeechModel = Model,
        StartUiAtLogin = StartUiAtLogin,
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
        _tools.Clear();
        _tools.AddRange(snapshot.Tools.Select(t => new ToolToggle(t, !_loaded.DisabledTools.Contains(t.Id, StringComparer.OrdinalIgnoreCase))));
        ScopeLocked = snapshot.PolicyForcesAllWindows;
        CaptureAllWindows = snapshot.PolicyForcesAllWindows || _loaded.CaptureAllWindows;
        _allWindowsConfirmed = CaptureAllWindows;
        AutoStart = _loaded.AutoStart;
        DefaultGraceSeconds = snapshot.DefaultGraceSeconds;
        GraceSeconds = _loaded.GraceSeconds ?? snapshot.DefaultGraceSeconds;
        Sensitivity = _loaded.Sensitivity;
        Microphones = snapshot.Microphones;
        Model = _loaded.SpeechModel;
        StartUiAtLogin = _loaded.StartUiAtLogin;
        _hotkeys.Clear();
        foreach (var (name, text) in _loaded.Hotkeys)
        {
            if (Enum.TryParse<HotkeyAction>(name, ignoreCase: false, out var action))
            {
                _hotkeys[action] = text;
            }
        }

        Loaded = true;
    }

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
}

/// <summary>Settings → Capture's gateway, over the pipe (ST-080).</summary>
public sealed class PipeCapture(CaptureConnection connection) : ICaptureGateway
{
    private readonly CaptureConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<CaptureSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        var reported = await _connection.RequestAsync<CaptureSettingsReported>(id => new GetCaptureSettingsCommand { RequestId = id }, ct).ConfigureAwait(false);
        return reported is null
            ? null
            : new CaptureSnapshot(
                reported.Settings,
                [.. reported.Tools.Select(t => new ToolChoice(t.Id, t.DisplayName))],
                reported.Microphones,
                reported.PolicyForcesAllWindows,
                reported.DefaultGraceSeconds);
    }

    public async Task<string?> SaveAsync(CaptureSettings settings, CancellationToken ct = default)
    {
        var result = await _connection.SendAsync(id => new SetCaptureSettingsCommand { RequestId = id, Settings = settings }, ct).ConfigureAwait(false);
        return result.Ok ? null : result.Error ?? "The capture service refused the settings.";
    }
}
