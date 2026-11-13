using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
    /// <c>Enter</c> on a row (spec v0.6.9 S4): its session opens, if the arrows have not opened it already,
    /// and the shell puts keyboard focus in the pane, where the review is done.
    /// </summary>
    public event EventHandler? RowActivated;

    private void OnRowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || Keyboard.Modifiers != ModifierKeys.None
            || DataContext is not InboxRailViewModel model
            || (e.OriginalSource as FrameworkElement)?.DataContext is not InboxRowViewModel row)
        {
            return;
        }

        e.Handled = true;
        model.Open(row);
        RowActivated?.Invoke(this, EventArgs.Empty);
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
