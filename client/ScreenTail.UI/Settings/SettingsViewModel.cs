using ScreenTail.UI.Settings.Activation;
using ScreenTail.UI.Settings.Capture;
using ScreenTail.UI.Settings.Integrations;
using ScreenTail.UI.Settings.Privacy;

namespace ScreenTail.UI.Settings;

/// <summary>
/// The Settings screen (Spec §5 S5–S7). This device's activation (ST-010) and Integrations (ST-082) are
/// built; Capture and Privacy &amp; Redaction are named with the ticket that brings them rather than
/// drawn as empty forms.
/// </summary>
public sealed class SettingsViewModel(ActivationViewModel activation, CaptureViewModel capture, PrivacyViewModel privacy, IntegrationsViewModel integrations)
{
    public ActivationViewModel Activation { get; } = activation ?? throw new ArgumentNullException(nameof(activation));

    public CaptureViewModel Capture { get; } = capture ?? throw new ArgumentNullException(nameof(capture));

    public PrivacyViewModel Privacy { get; } = privacy ?? throw new ArgumentNullException(nameof(privacy));

    public IntegrationsViewModel Integrations { get; } = integrations ?? throw new ArgumentNullException(nameof(integrations));

}
