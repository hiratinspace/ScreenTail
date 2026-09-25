using ScreenTail.UI.Settings.Integrations;

namespace ScreenTail.UI.Settings;

/// <summary>
/// The Settings screen (Spec §5 S5–S7). Integrations is built (ST-082); Capture and Privacy &amp;
/// Redaction are named with the ticket that brings them rather than drawn as empty forms.
/// </summary>
public sealed class SettingsViewModel(IntegrationsViewModel integrations)
{
    public IntegrationsViewModel Integrations { get; } = integrations ?? throw new ArgumentNullException(nameof(integrations));

    public string CaptureLater { get; } = "Capture settings — hotkeys, scope, exclusions — arrive with ST-080.";

    public string PrivacyLater { get; } = "Privacy & Redaction — local-only, patterns, retention, telemetry, delete everything — arrives with ST-081.";
}
