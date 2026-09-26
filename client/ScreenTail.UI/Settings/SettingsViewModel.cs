using ScreenTail.UI.Settings.Activation;
using ScreenTail.UI.Settings.Integrations;

namespace ScreenTail.UI.Settings;

/// <summary>
/// The Settings screen (Spec §5 S5–S7). This device's activation (ST-010) and Integrations (ST-082) are
/// built; Capture and Privacy &amp; Redaction are named with the ticket that brings them rather than
/// drawn as empty forms.
/// </summary>
public sealed class SettingsViewModel(ActivationViewModel activation, IntegrationsViewModel integrations)
{
    public ActivationViewModel Activation { get; } = activation ?? throw new ArgumentNullException(nameof(activation));

    public IntegrationsViewModel Integrations { get; } = integrations ?? throw new ArgumentNullException(nameof(integrations));

    public string CaptureLater { get; } = "Capture settings — hotkeys, scope, exclusions — arrive with ST-080.";

    public string PrivacyLater { get; } = "Privacy & Redaction — local-only, patterns, retention, telemetry, delete everything — arrives with ST-081.";
}
