using System.Windows.Controls;

namespace ScreenTail.UI.History;

/// <summary>
/// Layout and one gesture: a row the technician selects (a click, or the arrows) is opened. Which rows
/// there are, what they say and what opening does are <see cref="InboxRailViewModel"/>'s.
/// </summary>
public partial class InboxRailView : UserControl
{
    public InboxRailView()
    {
        InitializeComponent();
    }

    /// <summary><c>Ctrl+F</c>: focus in the search, its text selected so typing replaces it.</summary>
    public void FocusSearch()
    {
        _ = Search.Focus();
        Search.SelectAll();
    }

    /// <summary>
    /// A row became selected: open it. A row the model selected because its session is already open is
    /// not opened again (<see cref="InboxRailViewModel.Open"/> passes over an open row).
    /// </summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is InboxRailViewModel model && e.AddedItems.Count > 0 && e.AddedItems[0] is InboxRowViewModel row)
        {
            model.Open(row);
        }
    }
}
