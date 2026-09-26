using System.Text.Json.Serialization;

namespace ScreenTail.Shared.Settings;

/// <summary>
/// The technician's capture settings (ST-080, Spec §5 S5), as the service keeps them on disk and the UI
/// edits them. Absent values mean the shipped defaults: no grace means the registry's, no hotkey means
/// the shipped chord, no model means the one chosen for this machine's cores.
/// </summary>
public sealed record CaptureSettings
{
    public const int MinGraceSeconds = 30;

    public const int MaxGraceSeconds = 300;

    /// <summary>Remote tools switched off: their ids from the registry. A window of one starts nothing and is not captured.</summary>
    [JsonPropertyName("disabled_tools")]
    public IReadOnlyList<string> DisabledTools { get; init; } = [];

    /// <summary>Spec §5 S5 "All windows during a session". Warned about and confirmed before it is saved (INV-5).</summary>
    [JsonPropertyName("capture_all_windows")]
    public bool CaptureAllWindows { get; init; }

    /// <summary>Whether a remote tool taking focus starts a session by itself. Off means Ctrl+Alt+R only.</summary>
    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; init; } = true;

    /// <summary>How long after the remote tool goes away the session stops, 30–300 s. Null is the registry's.</summary>
    [JsonPropertyName("grace_seconds")]
    public int? GraceSeconds { get; init; }

    /// <summary>Hotkey chords by action name (<c>StartCapture</c>, <c>PauseOrResume</c>, <c>StopAndDraft</c>, <c>MarkMoment</c>), as text like <c>Ctrl+Alt+R</c>. Missing means the shipped chord.</summary>
    [JsonPropertyName("hotkeys")]
    public IReadOnlyDictionary<string, string> Hotkeys { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary><c>low</c>, <c>medium</c> or <c>high</c>: how much the screen has to change for a scene frame.</summary>
    [JsonPropertyName("sensitivity")]
    public string Sensitivity { get; init; } = "medium";

    /// <summary>The speech model's name (<c>tiny.en</c>, <c>base.en</c>, <c>small.en</c>), or null for the one chosen for this machine. Applies at the next start.</summary>
    [JsonPropertyName("speech_model")]
    public string? SpeechModel { get; init; }

    [JsonPropertyName("start_ui_at_login")]
    public bool StartUiAtLogin { get; init; }
}
