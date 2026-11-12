using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenTail.Core.Review;
using ScreenTail.Core.Shell;
using ScreenTail.UI.Render;
using ScreenTail.UI.Review;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace ScreenTail.UI.Shell;

/// <summary>
/// The renders the first pass did not make (ST-016, 2026-11-11 design critique): every Settings section
/// at its full height, keyboard focus on each kind of control, 150% DPI, the typed confirmations and the
/// enlarged frame. Harness only: nothing here runs in the application.
/// </summary>
#pragma warning disable CA1001 // The other half of this class explains: the window disposes its model in OnClosed.
public partial class ShellWindow
#pragma warning restore CA1001
{
    /// <summary>The Settings sections, by the pane type that draws each and the heading it sits under.</summary>
    private static readonly (string Name, Type Pane)[] SettingsSections =
    [
        ("activation", typeof(Settings.Activation.ActivationPane)),
        ("capture", typeof(Settings.Capture.CapturePane)),
        ("privacy", typeof(Settings.Privacy.PrivacyPane)),
        ("integrations", typeof(Settings.Integrations.IntegrationsPane)),
        ("updates", typeof(Update.UpdatesPane)),
    ];

    private string Shot(string screen, RenderTheme theme) => Path.Combine(_screenshotDirectory!, RenderThemes.FileName(screen, theme));

    private static async Task SettleAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Render);
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    /// <summary>
    /// Waits for the Review pane's strip to have every thumbnail the fixture has a picture for.
    ///
    /// They load after the pane is up, and dark is the first theme drawn after a session opens, so the
    /// first version drew frame 1 as a placeholder in dark and as the screenshot in every later theme.
    /// </summary>
    private async Task ThumbnailsAsync()
    {
        if (_model.Content is not ReviewViewModel review)
        {
            return;
        }

        var directory = SessionFixture.Directory();
        var expected = review.Strip.Items.OfType<FrameItem>()
            .Where(item => File.Exists(Path.Combine(directory, item.Cell.Frame.Image)))
            .ToList();
        for (var waited = 0; waited < 1000 && expected.Any(item => item.Thumbnail is null); waited += 25)
        {
            await Task.Delay(25).ConfigureAwait(true);
        }

        // The first run waited five seconds per screen and none ever arrived: on a pane's first show the
        // strip's own load did not fill them, and they appeared only once a theme change re-templated the
        // view. So the harness asks the strip for them itself, which is the same call the view makes, and
        // says that it had to.
        var missing = expected.Count(item => item.Thumbnail is null);
        if (missing > 0)
        {
            Console.WriteLine($"  the strip had loaded {expected.Count - missing} of {expected.Count} thumbnails by itself after 1 s; loading the rest");
            await review.Strip.LoadImagesAsync().ConfigureAwait(true);
            missing = expected.Count(item => item.Thumbnail is null);
            if (missing > 0)
            {
                throw new InvalidOperationException($"{missing} of {expected.Count} fixture thumbnails would not load.");
            }
        }

        await SettleAsync();
    }

    /// <summary>Every Settings section, each in a render tall enough to hold all of it under the shell's header.</summary>
    private async Task SettingsSectionsAsync(ShellState state)
    {
        state.Navigate(ShellView.Settings);
        foreach (var theme in RenderThemes.All)
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            await SettleAsync();
            foreach (var (name, type) in SettingsSections)
            {
                var natural = WindowRenderer.Lay(this);
                var pane = Find(this, element => type.IsInstanceOfType(element)) as FrameworkElement;
                var scroller = pane is null ? null : Ancestor<ScrollViewer>(pane);
                if (pane is null || scroller?.Content is not FrameworkElement page)
                {
                    throw new InvalidOperationException($"Settings has no {type.Name} in a scrolling page.");
                }

                var (top, bottom) = Section(pane, page);
                var height = natural.Height + Math.Max(0, bottom - top - scroller.ViewportHeight);
                _ = WindowRenderer.Lay(this, height);
                scroller.ScrollToVerticalOffset(top);
                scroller.UpdateLayout();
                WindowRenderer.Save(this, Shot($"settings-{name}", theme), height);
            }
        }

        // Back to the window's own height, and to the top, for whatever renders next.
        var settings = Find(this, element => element is Settings.SettingsPane);
        if (settings is not null && Find(settings, e => e is ScrollViewer) is ScrollViewer back)
        {
            back.ScrollToTop();
        }
    }

    /// <summary>
    /// Where a section starts and ends on the page: from its heading (the line above its pane, or the
    /// group that holds both) to the bottom of its pane.
    /// </summary>
    private static (double Top, double Bottom) Section(FrameworkElement pane, FrameworkElement page)
    {
        FrameworkElement start = pane;
        if (VisualTreeHelper.GetParent(pane) is StackPanel group && group != page)
        {
            start = group;
        }
        else if (VisualTreeHelper.GetParent(pane) is Grid grid)
        {
            var row = Grid.GetRow(pane);
            start = grid.Children.OfType<FrameworkElement>().FirstOrDefault(c => Grid.GetRow(c) == row - 1 && c is TextBlock) ?? pane;
        }

        var top = start.TranslatePoint(new Point(0, -start.Margin.Top), page).Y;
        var bottom = pane.TranslatePoint(new Point(0, pane.ActualHeight + 16), page).Y;
        return (Math.Max(0, top), bottom);
    }

    /// <summary>
    /// Keyboard focus on a button, a text box and a check box in Settings, and on a row in History, so the
    /// focus ring can be reviewed in every theme. WPF draws a FocusVisualStyle only when focus arrived by
    /// keyboard, so the harness makes the keyboard the last input first.
    /// </summary>
    private async Task FocusAsync(ShellState state)
    {
        foreach (var theme in RenderThemes.All)
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            state.Navigate(ShellView.Settings);
            await SettleAsync();
            _ = WindowRenderer.Lay(this);
            var settings = Find(this, element => element is Settings.SettingsPane)
                ?? throw new InvalidOperationException("No Settings pane to focus in.");
            foreach (var (name, pick) in new (string, Func<DependencyObject, bool>)[]
            {
                ("button", e => e is Button),
                ("input", e => e is TextBox),
                ("checkbox", e => e is CheckBox),
            })
            {
                await FocusAndSaveAsync(Find(settings, e => pick(e) && Usable(e)), $"focus-{name}", theme);
            }

            // A table row draws no ring of its own (Table.Row sets none); keyboard focus in a DataGrid sits
            // on a cell, and the cell is what draws it. The rows load after the pane, so they are waited for.
            state.Navigate(ShellView.History);
            DependencyObject? cell = null;
            for (var waited = 0; waited < 2000 && cell is null; waited += 50)
            {
                await SettleAsync();
                _ = WindowRenderer.Lay(this);
                cell = Find(this, e => e is DataGridCell && Usable(e));
                if (cell is null)
                {
                    await Task.Delay(50).ConfigureAwait(true);
                }
            }

            await FocusAndSaveAsync(cell, "focus-list-row", theme);
        }

        Keyboard.ClearFocus();
    }

    private async Task FocusAndSaveAsync(DependencyObject? target, string screen, RenderTheme theme)
    {
        if (target is not FrameworkElement element)
        {
            throw new InvalidOperationException($"{screen}: nothing to focus.");
        }

        if (PresentationSource.FromVisual(this) is { } source)
        {
            // A key the window does nothing with, so the keyboard is the most recent input device and
            // focus that moves next is drawn as keyboard focus.
            InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.LeftShift) { RoutedEvent = Keyboard.KeyUpEvent });
        }

        element.BringIntoView();
        _ = Keyboard.Focus(element);
        await SettleAsync();

        // The first hosted run focused all three controls and WPF drew no ring for any of them: it decides
        // "focus arrived by keyboard" from input this harness cannot really send. So the ring is drawn the
        // way WPF draws it, the element's own FocusVisualStyle on a Control in an adorner over it, and taken
        // away after the render. What is reviewed is the style the product ships, not a stand-in.
        Adorner? drawn = null;
        if (!HasAdorner(element) && element.FocusVisualStyle is { } style && AdornerLayer.GetAdornerLayer(element) is { } layer)
        {
            drawn = new FocusRingAdorner(element, style);
            layer.Add(drawn);
            await SettleAsync();
        }

        var ring = drawn is not null ? "its FocusVisualStyle drawn by the harness"
            : HasAdorner(element) ? "focus ring drawn by WPF"
            : "no FocusVisualStyle; the control's own focused trigger is what shows";
        Console.WriteLine($"  {screen}/{theme.Suffix()}: {element.GetType().Name} focused={element.IsKeyboardFocused}, {ring}");
        WindowRenderer.Save(this, Shot(screen, theme));
        if (drawn is not null)
        {
            AdornerLayer.GetAdornerLayer(element)?.Remove(drawn);
        }
    }

    /// <summary>What WPF's own focus visual is: a Control styled with the element's FocusVisualStyle, sized to the element.</summary>
    private sealed class FocusRingAdorner : Adorner
    {
        private readonly Control _ring;

        public FocusRingAdorner(UIElement adorned, Style style)
            : base(adorned)
        {
            _ring = new Control { Style = style, Focusable = false };
            IsHitTestVisible = false;
            AddVisualChild(_ring);
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _ring;

        protected override Size MeasureOverride(Size constraint)
        {
            _ring.Measure(AdornedElement.RenderSize);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _ring.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }

    private static bool HasAdorner(UIElement element) =>
        AdornerLayer.GetAdornerLayer(element)?.GetAdorners(element) is { Length: > 0 };

    private static bool Usable(DependencyObject element) =>
        element is UIElement { IsVisible: true, IsEnabled: true, Focusable: true };

    /// <summary>Review, History and Settings at 150%, in dark and light.</summary>
    private async Task HighDpiAsync(ShellState state)
    {
        foreach (var (name, arrange) in new (string, Action)[]
        {
            ("review", () => state.OpenSession("preview")),
            ("history", () => state.Navigate(ShellView.History)),
            ("settings", () => state.Navigate(ShellView.Settings)),
        })
        {
            arrange();
            await ThumbnailsAsync();
            foreach (var theme in new[] { RenderTheme.Dark, RenderTheme.Light })
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await SettleAsync();
                WindowRenderer.Save(this, Shot($"shell-{name}-150pct", theme), scale: 1.5);
            }
        }
    }

    /// <summary>The enlarged frame over the Review pane, opened as Enter opens it.</summary>
    private async Task EnlargedAsync(ShellState state)
    {
        state.OpenSession("preview");
        await ThumbnailsAsync();
        if (_model.Content is not ReviewViewModel review || review.Strip.Items.OfType<FrameItem>().FirstOrDefault(f => f.Thumbnail is not null) is not { } frame)
        {
            throw new InvalidOperationException("The Review pane has no frame to enlarge.");
        }

        await review.Strip.EnlargeCommand.ExecuteAsync(frame);
        foreach (var theme in RenderThemes.All)
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            await SettleAsync();
            Save(Shot("shell-review-enlarged", theme));
        }

        review.Strip.CloseEnlargedCommand.Execute(null);
        await SettleAsync();
    }

    /// <summary>Both typed confirmations: the shell's (Delete everything) and Review's (Discard).</summary>
    private async Task DialogsAsync()
    {
        foreach (var (name, build) in new (string, Func<Window>)[]
        {
            ("dialog-delete-everything", () => new ConfirmWindow(LiveShell.EraseHeading, LiveShell.EraseConsequence, "DELETE EVERYTHING")),
            ("dialog-discard-session", () => ConfirmTypedDialog.ForDiscard(TypedConfirmation.ForDiscard())),
        })
        {
            foreach (var theme in RenderThemes.All)
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                var dialog = WindowRenderer.Intend(build());
                dialog.ShowInTaskbar = false;
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                dialog.Left = -4000;
                dialog.Top = -4000;
                dialog.Show();
                await SettleAsync();
                dialog.UpdateLayout();
                Save(dialog, Shot(name, theme));
                dialog.Close();
            }
        }
    }

    private static DependencyObject? Find(DependencyObject root, Func<DependencyObject, bool> match)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (next != root && match(next))
            {
                return next;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(next); i++)
            {
                queue.Enqueue(VisualTreeHelper.GetChild(next, i));
            }
        }

        return null;
    }

    private static T? Ancestor<T>(DependencyObject from)
        where T : DependencyObject
    {
        for (var at = VisualTreeHelper.GetParent(from); at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is T found)
            {
                return found;
            }
        }

        return null;
    }
}
