using ScreenTail.UI.Settings.Activation;
using ScreenTail.UI.Settings.Capture;
using ScreenTail.UI.Settings.Integrations;
using ScreenTail.UI.Settings.Privacy;
using ScreenTail.UI.Update;

namespace ScreenTail.UI.Settings;

/// <summary>
/// The Settings screen (Spec §5 S5–S7). This device's activation (ST-010), Capture (ST-080), Privacy
/// &amp; Redaction (ST-081), Integrations (ST-082) and Updates (ST-113).
/// </summary>
public sealed class SettingsViewModel(ActivationViewModel activation, CaptureViewModel capture, PrivacyViewModel privacy, IntegrationsViewModel integrations, UpdatesViewModel updates)
{
    public ActivationViewModel Activation { get; } = activation ?? throw new ArgumentNullException(nameof(activation));

    public CaptureViewModel Capture { get; } = capture ?? throw new ArgumentNullException(nameof(capture));

    public PrivacyViewModel Privacy { get; } = privacy ?? throw new ArgumentNullException(nameof(privacy));

    public IntegrationsViewModel Integrations { get; } = integrations ?? throw new ArgumentNullException(nameof(integrations));

    public UpdatesViewModel Updates { get; } = updates ?? throw new ArgumentNullException(nameof(updates));
}
