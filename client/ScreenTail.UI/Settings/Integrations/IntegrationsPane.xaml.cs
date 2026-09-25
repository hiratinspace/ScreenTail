using System.Windows;
using System.Windows.Controls;

namespace ScreenTail.UI.Settings.Integrations;

/// <summary>
/// Layout only. Every rule is <see cref="Core.Settings.IntegrationCard"/>'s and every binding
/// <see cref="IntegrationCardViewModel"/>'s. The two handlers exist because WPF's PasswordBox does not
/// bind: one copies what was typed to the view model, the other clears the box the moment it leaves
/// the screen, so a saved private key is not kept by a collapsed control.
/// </summary>
public partial class IntegrationsPane : UserControl
{
    public IntegrationsPane()
    {
        InitializeComponent();
    }

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: IntegrationCardViewModel card } box)
        {
            if (box.Tag as string == "api")
            {
                card.ApiKey = box.Password;
            }
            else
            {
                card.PrivateKey = box.Password;
            }
        }
    }

    private void OnSecretBoxVisibility(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is PasswordBox box && e.NewValue is false)
        {
            box.Clear();
        }
    }
}
