using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScreenTail.UI.Review.Publish;

/// <summary>
/// Layout only. Every rule is <see cref="Core.Review.Publish.PublishPanel"/>'s, every binding
/// <see cref="PublishViewModel"/>'s. What is here is focus, which WPF offers no command for: the search
/// box takes focus when the picker opens (Ctrl+T), Change takes it back when the picker closes, and the
/// search's focus asks for the recent tickets.
/// </summary>
public partial class EnvelopeView : UserControl
{
    private PublishViewModel? _model;

    public EnvelopeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = e.NewValue as PublishViewModel;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PublishViewModel.PickerVisibility) || _model is null)
        {
            return;
        }

        // After layout, so the box is visible and can take focus.
        var opening = _model.PickerVisibility == Visibility.Visible;
        var wasInside = TicketSearch.IsKeyboardFocusWithin || TicketMatches.IsKeyboardFocusWithin;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (opening)
            {
                _ = Keyboard.Focus(TicketSearch);
            }
            else if (wasInside && ChangeButton.IsVisible)
            {
                _ = Keyboard.Focus(ChangeButton);
            }
        });
    }

    private void OnTicketFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is PublishViewModel pane && pane.ShowRecentCommand.CanExecute(null))
        {
            pane.ShowRecentCommand.Execute(null);
        }
    }

    /// <summary>The arrows move through the matches; Enter takes the highlighted one and Esc leaves it as it was.</summary>
    private void OnMatchesKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape && DataContext is PublishViewModel pane)
        {
            pane.CloseTicketPickerCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnMatchClicked(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is PublishViewModel pane && TicketMatches.SelectedItem is not null)
        {
            pane.CloseTicketPickerCommand.Execute(null);
        }
    }
}
