using System.Windows;
using System.Windows.Input;

namespace ScreenTail.UI.Controls;

/// <summary>
/// Says when a table's row should draw the keyboard focus outline (Spec §3, §7; v0.6.6).
///
/// In a DataGrid keyboard focus sits on a cell, never on the row, so a row's FocusVisualStyle is never
/// drawn and a cell's draws a ring round one cell of a table that selects whole rows. The row cannot use
/// IsKeyboardFocusWithin alone either: that is also true after a click, and the focus ring is for the
/// keyboard only. So this watches the row and sets <see cref="ShownProperty"/> the way WPF decides to
/// draw a focus visual: focus is inside the row, and the keyboard was the last device used when it
/// arrived or has been used since.
///
/// Switched on from the row's style with <c>controls:RowFocus.Track="True"</c>; the row's template
/// draws the outline while Shown is true.
/// </summary>
public static class RowFocus
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track",
        typeof(bool),
        typeof(RowFocus),
        new PropertyMetadata(false, OnTrackChanged));

    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown",
        typeof(bool),
        typeof(RowFocus),
        new PropertyMetadata(false));

    public static bool GetTrack(DependencyObject element) => element is not null && (bool)element.GetValue(TrackProperty);

    public static void SetTrack(DependencyObject element, bool value) => element?.SetValue(TrackProperty, value);

    public static bool GetShown(DependencyObject element) => element is not null && (bool)element.GetValue(ShownProperty);

    /// <summary>Set by the tracking here; the screenshot harness also sets it, to draw the outline where it cannot send a key.</summary>
    internal static void SetShown(DependencyObject element, bool value) => element?.SetValue(ShownProperty, value);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement row)
        {
            return;
        }

        row.IsKeyboardFocusWithinChanged -= OnFocusWithinChanged;
        row.PreviewKeyDown -= OnPreviewKeyDown;
        if ((bool)e.NewValue)
        {
            row.IsKeyboardFocusWithinChanged += OnFocusWithinChanged;
            row.PreviewKeyDown += OnPreviewKeyDown;
        }
        else
        {
            SetShown(row, false);
        }
    }

    private static void OnFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement row)
        {
            SetShown(row, row.IsKeyboardFocusWithin && InputManager.Current.MostRecentInputDevice is KeyboardDevice);
        }
    }

    /// <summary>A key pressed in a row that was clicked into: the keyboard is in use now, so the outline shows.</summary>
    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is UIElement { IsKeyboardFocusWithin: true } row)
        {
            SetShown(row, true);
        }
    }
}
