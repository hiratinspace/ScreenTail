using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScreenTail.UI.Review.Publish;

/// <summary>
/// The receipt (Spec v0.6.0 §5 S3). Layout only: every rule is <see cref="Core.Review.Publish.PublishPanel"/>'s,
/// every binding <see cref="PublishViewModel"/>'s. What is here is focus: when the receipt appears,
/// focus goes to Open next, so Enter opens it, as the reference draws it; with nothing next, to the
/// receipt's first control. And Enter on a check box does not open the next session: it is a key a
/// technician may press there by habit, and leaving the receipt is not what they meant.
/// </summary>
public partial class PublishPane : UserControl
{
    public PublishPane()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;

        // The next session is read after the receipt appears, so Open next can show a moment later: it
        // takes focus then, if focus is still on the receipt or nowhere.
        OpenNextButton.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && (IsKeyboardFocusWithin || Keyboard.FocusedElement is null))
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Keyboard.Focus(OpenNextButton));
            }
        };
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.Enter && e.OriginalSource is CheckBox)
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (OpenNextButton.IsVisible)
            {
                _ = Keyboard.Focus(OpenNextButton);
            }
            else
            {
                _ = MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        });
    }
}
