using System.Windows.Controls;

namespace ScreenTrail.UI.Settings.Integrations;

/// <summary>
/// Layout only. Every rule is <see cref="Core.Settings.IntegrationCard"/>'s and every binding
/// <see cref="IntegrationCardViewModel"/>'s. The cards are read only, so nothing secret is ever typed
/// here: integration credentials can only be changed by the tenant owner, in the web admin.
/// </summary>
public partial class IntegrationsPane : UserControl
{
    public IntegrationsPane()
    {
        InitializeComponent();
    }
}
