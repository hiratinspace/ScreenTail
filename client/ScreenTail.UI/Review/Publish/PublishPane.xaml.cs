using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTail.UI.Review.Publish;

/// <summary>
/// Layout only. Every rule is <see cref="Core.Review.Publish.PublishPanel"/>'s, every binding
/// <see cref="PublishViewModel"/>'s. The one handler here turns focus, which WPF offers no command for,
/// into the view model's command.
/// </summary>
public partial class PublishPane : UserControl
{
    public PublishPane()
    {
        InitializeComponent();
    }

    private void OnTicketFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is PublishViewModel pane && pane.ShowRecentCommand.CanExecute(null))
        {
            pane.ShowRecentCommand.Execute(null);
        }
    }
}
