using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTail.UI.History;

/// <summary>Layout and one gesture. What a row is and what opening it does are <see cref="HistoryViewModel"/>'s.</summary>
public partial class HistoryPane : UserControl
{
    public HistoryPane()
    {
        InitializeComponent();
    }

    private void OnOpen(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HistoryViewModel model && Sessions.SelectedItem is HistoryRow row)
        {
            model.OpenCommand.Execute(row);
        }
    }
}
