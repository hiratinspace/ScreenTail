using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenTail.UI.Settings;

/// <summary>
/// Layout, and the three things only the view can do: bring a section into view when the nav or the Save
/// bar's link asks (and select the section the page is scrolled to), keep the focused control in view when
/// the bar appears under it, and send Esc to the bar's Undo only while focus is on the bar. Everything it
/// shows is <see cref="SettingsViewModel"/>'s.
/// </summary>
public partial class SettingsPane : UserControl
{
    /// <summary>Set while the view moves the selection itself, so scrolling to a section does not scroll again.</summary>
    private bool _following;

    public SettingsPane()
    {
        InitializeComponent();
        Page.GotKeyboardFocus += OnPageGotKeyboardFocus;
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SettingsViewModel old)
            {
                old.JumpRequested -= OnJumpRequested;
            }

            if (e.NewValue is SettingsViewModel model)
            {
                model.JumpRequested += OnJumpRequested;
            }
        };
    }

    /// <summary>The nav's selection moved (a click or the arrows): bring that section to the top of the page.</summary>
    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_following && Nav.SelectedItem is SettingsNavItem item && (Nav.IsKeyboardFocusWithin || Nav.IsMouseOver))
        {
            _ = ScrollTo(item.Key);
        }
    }

    /// <summary>The bar's link: the section to the top of the page, and focus on its first control.</summary>
    private void OnJumpRequested(object? sender, string key)
    {
        if (ScrollTo(key) is not { } group)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!group.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)))
            {
                _ = Nav.Focus();
            }
        });
    }

    private FrameworkElement? ScrollTo(string key)
    {
        var group = Page.Children.OfType<FrameworkElement>().FirstOrDefault(c => c.Tag as string == key);
        if (group is null)
        {
            return null;
        }

        var top = group.TranslatePoint(new Point(0, 0), Page).Y;
        Scroller.ScrollToVerticalOffset(Math.Max(0, top));
        return group;
    }

    /// <summary>The page scrolled: the nav shows the section at the top of the view as current.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // A text box or list inside the page has a scroller of its own, whose changes arrive here too.
        if (!ReferenceEquals(e.OriginalSource, Scroller) || DataContext is not SettingsViewModel model || e.VerticalChange == 0 || Nav.IsKeyboardFocusWithin)
        {
            return;
        }

        var offset = Scroller.VerticalOffset + 8;
        string? current = null;
        foreach (var group in Page.Children.OfType<FrameworkElement>())
        {
            if (group.TranslatePoint(new Point(0, 0), Page).Y <= offset)
            {
                current = group.Tag as string;
            }
        }

        // At the very bottom the last section is current, however short it is.
        if (Scroller.ScrollableHeight > 0 && Scroller.VerticalOffset >= Scroller.ScrollableHeight - 1)
        {
            current = Page.Children.OfType<FrameworkElement>().LastOrDefault()?.Tag as string;
        }

        if (current is not null && model.SelectedSection?.Key != current)
        {
            _following = true;
            model.SelectedSection = model.Sections.FirstOrDefault(s => s.Key == current);
            _following = false;
        }
    }

    /// <summary>
    /// Focus reached the page's first stop, coming back up it: nothing above that stop takes focus (an
    /// activated licence is only text), so the page goes back to its top when the stop still fits there.
    /// The keyboard can then reach what is above it again, and the nav's section is the one Tab left it
    /// at, so Shift+Tab into the nav retraces Tab instead of landing on whichever section the minimal
    /// scroll left at the top.
    /// </summary>
    private void OnPageGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is not FrameworkElement focused || Scroller.VerticalOffset == 0 || FirstStop(Page) is not { } first || !IsInside(focused, first))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ReferenceEquals(Keyboard.FocusedElement, focused) && focused.TranslatePoint(new Point(0, focused.ActualHeight), Page).Y <= Scroller.ViewportHeight)
            {
                Scroller.ScrollToVerticalOffset(0);
            }
        });
    }

    /// <summary>The first control in the page Tab can stop on, in tree order (the page is one column).</summary>
    private static Control? FirstStop(DependencyObject at)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(at); i++)
        {
            var child = VisualTreeHelper.GetChild(at, i);
            if (child is UIElement { IsVisible: false })
            {
                continue;
            }

            if (child is Control { Focusable: true, IsEnabled: true, IsTabStop: true } control)
            {
                return control;
            }

            if (FirstStop(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The bar appeared: the page's view just got shorter, so the control being typed in is scrolled back
    /// into it rather than left under the bar. It disappeared with focus on it (after Save or Undo): focus
    /// goes to the nav rather than to nowhere.
    /// </summary>
    private void OnBarVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var shown = e.NewValue is true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (shown)
            {
                if (focused is FrameworkElement element && IsInside(element, Scroller))
                {
                    element.BringIntoView();
                }

                return;
            }

            if (IsVisible && (focused is null || !IsInside(focused, this)))
            {
                _ = (Nav.ItemContainerGenerator.ContainerFromItem(Nav.SelectedItem) as UIElement ?? Nav).Focus();
            }
        });
    }

    /// <summary>Esc while focus is on the bar is Undo changes (<see cref="SettingsViewModel.UndoFromEscape"/>).</summary>
    private void OnBarKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && DataContext is SettingsViewModel model)
        {
            e.Handled = model.UndoFromEscape();
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var at = element; at is not null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at == ancestor)
            {
                return true;
            }
        }

        return false;
    }
}
