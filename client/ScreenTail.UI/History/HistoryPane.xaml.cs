using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTail.UI.History;

/// <summary>
/// Layout and three gestures: a double-click opens a row, the table's selection is handed to the model
/// (a DataGrid's selected items are not bindable), and focus goes back to the table when a discard has
/// taken the focused control away. What a row is, which rows qualify and what discarding does are
/// <see cref="HistoryViewModel"/>'s.
/// </summary>
public partial class HistoryPane : UserControl
{
    private HistoryViewModel? _listening;

    public HistoryPane()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnOpen(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HistoryViewModel model && Sessions.SelectedItem is HistoryRow row)
        {
            model.OpenCommand.Execute(row);
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is HistoryViewModel model)
        {
            model.Select(Sessions.SelectedItems.OfType<HistoryRow>());
        }
    }

    /// <summary>
    /// Listens for the model's discards while on screen, and not after: the model outlives every pane the
    /// shell builds for it.
    ///
    /// Shown after Review's Discard, whose button went with the pane it was in, focus is on nothing a
    /// keyboard user can see, so it goes to the list, where the discarded session no longer waits for
    /// review. Arriving any other way (the History tab, say) leaves focus where the technician put it.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is HistoryViewModel model && !ReferenceEquals(model, _listening))
        {
            OnUnloaded(sender, e);
            model.Discarded += OnDiscarded;
            _listening = model;
        }

        if (Keyboard.FocusedElement is not UIElement { IsVisible: true })
        {
            FocusTheList();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_listening is not null)
        {
            _listening.Discarded -= OnDiscarded;
            _listening = null;
        }
    }

    /// <summary>After a bulk discard the button is disabled, having nothing selected, and would hold focus nowhere.</summary>
    private void OnDiscarded(object? sender, EventArgs e) => FocusTheList();

    private void FocusTheList()
    {
        if (Sessions.IsVisible)
        {
            _ = Sessions.Focus();
        }
    }
}
