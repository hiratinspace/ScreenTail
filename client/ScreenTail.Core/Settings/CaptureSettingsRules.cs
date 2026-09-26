using ScreenTail.Core.Capture;
using ScreenTail.Core.Input;
using ScreenTail.Core.Speech;
using ScreenTail.Shared.Settings;

namespace ScreenTail.Core.Settings;

/// <summary>The rules over <see cref="CaptureSettings"/> that need the engines: what it refuses and what it turns into.</summary>
public static class CaptureSettingsRules
{
    public static readonly IReadOnlyList<string> Sensitivities = ["low", "medium", "high"];

    public static IReadOnlyList<string> Problems(this CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = new List<string>();
        if (settings.GraceSeconds is { } grace && grace is < CaptureSettings.MinGraceSeconds or > CaptureSettings.MaxGraceSeconds)
        {
            problems.Add($"The grace period is between {CaptureSettings.MinGraceSeconds} and {CaptureSettings.MaxGraceSeconds} seconds.");
        }

        if (!Sensitivities.Contains(settings.Sensitivity, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("Scene-change sensitivity is Low, Medium or High.");
        }

        foreach (var (action, text) in settings.Hotkeys)
        {
            if (!Enum.TryParse<HotkeyAction>(action, ignoreCase: false, out _))
            {
                problems.Add($"\"{action}\" is not a hotkey action.");
            }
            else if (!Hotkey.TryParse(text, out var hotkey) || !hotkey.Value.IsUsable)
            {
                problems.Add($"The {action} hotkey \"{text}\" is not a chord like Ctrl+Alt+R.");
            }
        }

        foreach (var conflict in settings.Conflicts())
        {
            problems.Add($"The {conflict.Action} hotkey {conflict.Hotkey}: {conflict.Reason}{(conflict.Suggestion is { } s ? $" Try {s}." : string.Empty)}");
        }

        if (settings.SpeechModel is { } model && SpeechModels.All.All(m => !string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add($"\"{model}\" is not a speech model this build ships.");
        }

        return problems;
    }

    /// <summary>The shipped chords with the technician's on top. A chord that does not parse is left as shipped; <see cref="Problems"/> names it.</summary>
    public static HotkeyBindings ToBindings(this CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var bound = new Dictionary<HotkeyAction, Hotkey>(HotkeyBindings.Defaults);
        foreach (var (name, text) in settings.Hotkeys)
        {
            if (Enum.TryParse<HotkeyAction>(name, ignoreCase: false, out var action) && Hotkey.TryParse(text, out var hotkey))
            {
                bound[action] = hotkey.Value;
            }
        }

        return new HotkeyBindings(bound);
    }

    public static IReadOnlyList<HotkeyConflict> Conflicts(this CaptureSettings settings) => settings.ToBindings().Validate();

    /// <summary>Low needs a fifth of the screen to change and waits longer; High takes a twentieth and waits less; Medium is the shipped default.</summary>
    public static SceneSamplerOptions ToSceneOptions(this CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Sensitivity.ToLowerInvariant() switch
        {
            "low" => new SceneSamplerOptions { Threshold = 20, MinimumGap = TimeSpan.FromSeconds(5) },
            "high" => new SceneSamplerOptions { Threshold = 5, MinimumGap = TimeSpan.FromSeconds(2) },
            _ => new SceneSamplerOptions(),
        };
    }
}
