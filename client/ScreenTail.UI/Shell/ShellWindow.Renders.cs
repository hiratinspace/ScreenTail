using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ScreenTail.Core.Onboarding;
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

    /// <summary>
    /// Every Settings section, each in a render tall enough to hold all of it under the shell's header:
    /// first with nothing changed, then with a change pending in each section that has a Save path, so the
    /// "Changed" marks, the nav's counts and the one Save bar show (settings-pending-*), and then the bar
    /// after a Save that one section could not make (settings-save-failed). Undone afterwards.
    /// </summary>
    private async Task SettingsSectionsAsync(ShellState state)
    {
        state.Navigate(ShellView.Settings);
        await SettleAsync();
        await SettingsSectionsAsync("settings");
        if (_model.Content is not Settings.SettingsViewModel settings)
        {
            throw new InvalidOperationException("Settings has no view model to change.");
        }

        // Remove's question on the ConnectWise card (v0.6.11), asked on the card, then put away.
        settings.Integrations.ConnectWise.AskRemoveCommand.Execute(null);
        await SettleAsync();
        await SettingsSectionsAsync("settings-remove-question", only: "integrations");
        settings.Integrations.ConnectWise.KeepConnectionCommand.Execute(null);

        Pend(settings);
        await SettleAsync();
        if (settings.BarVisibility != Visibility.Visible)
        {
            throw new InvalidOperationException("The Save bar did not appear with changes pending.");
        }

        Console.WriteLine($"  Save bar: {settings.Summary} {settings.LinkSection} ({settings.BarName})");
        await SettingsSectionsAsync("settings-pending");

        // A Save that Capture cannot make: "All windows" chosen and not confirmed (INV-5). Privacy's change
        // commits, Capture's stays, and the bar says which and why.
        await settings.UndoCommand.ExecuteAsync(null);
        settings.Capture.AllWindows = true;
        settings.Privacy.Telemetry = !settings.Privacy.Telemetry;
        await settings.SaveCommand.ExecuteAsync(null);
        await SettleAsync();
        Console.WriteLine($"  Save bar after a failed save: {settings.FailureMessage}");
        foreach (var theme in RenderThemes.All)
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            await SettleAsync();
            Save(Shot("settings-save-failed", theme));
        }

        await settings.UndoCommand.ExecuteAsync(null);
        await SettleAsync();

        // Back to the window's own height, and to the top, for whatever renders next.
        var pane = Find(this, element => element is Settings.SettingsPane);
        if (pane is not null && Find(pane, e => e is ScrollViewer) is ScrollViewer back)
        {
            back.ScrollToTop();
        }
    }

    /// <summary>A change in every section the Save bar covers, as a technician would make them.</summary>
    private static void Pend(Settings.SettingsViewModel settings)
    {
        settings.Capture.AutoStart = !settings.Capture.AutoStart;
        settings.Capture.Sensitivity = settings.Capture.Sensitivity == "high" ? "low" : "high";
        settings.Privacy.Telemetry = !settings.Privacy.Telemetry;
        settings.Integrations.Hudu.ChangeCommand.Execute(null);
        settings.Integrations.Hudu.SiteUrl = "https://docs.contosomsp.com/kb";
        if (settings.Updates.ChannelEditable)
        {
            settings.Updates.Pilot = !settings.Updates.Pilot;
            settings.Updates.Stable = !settings.Updates.Pilot;
        }
    }

    private async Task SettingsSectionsAsync(string prefix, string? only = null)
    {
        foreach (var theme in RenderThemes.All)
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            await SettleAsync();
            foreach (var (name, type) in SettingsSections.Where(s => only is null || s.Name == only))
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

                // The scroll happens once the page is laid out at the render's height, which may be in a
                // host outside the window: scrolled before, it would be clamped to the window's shorter view.
                WindowRenderer.Save(this, Shot($"{prefix}-{name}", theme), height, prepare: () =>
                {
                    scroller.ScrollToVerticalOffset(top);
                    scroller.UpdateLayout();
                    return null;
                });
            }
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

            // Keyboard focus in a DataGrid sits on a cell, and the row draws the outline round itself
            // (Table.Row, RowFocus). The second row, selected as the arrows leave it, so the outline is seen
            // against the selection's tint and the rows above and below. The rows load after the pane, so
            // they are waited for.
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

            var sessions = Find(this, e => e is DataGrid) as DataGrid;
            if (sessions is { Items.Count: > 1 } && sessions.ItemContainerGenerator.ContainerFromIndex(1) is DataGridRow second)
            {
                sessions.SelectedItem = sessions.Items[1];
                await SettleAsync();
                cell = Find(second, e => e is DataGridCell && Usable(e)) ?? cell;
            }

            await FocusAndSaveAsync(cell, "focus-list-row", theme);
            sessions?.UnselectAll();
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
        // away after the render. What is reviewed is the style the product ships, not a stand-in. It is
        // added at the moment of the render, to whichever adorner layer the element is under then: the
        // window's, or the host's when the render draws the content outside the window.
        var byWpf = HasAdorner(element);
        var style = byWpf ? null : element.FocusVisualStyle;

        // A table's row draws its own outline while RowFocus says focus came to it by keyboard, which is
        // the same decision from the same input. Where the product did not make it here, the harness sets
        // what the product would have set, for the picture only: the outline drawn is still the row's own.
        var row = element is DataGridCell ? Ancestor<DataGridRow>(element) : null;
        var byRow = row is not null && Controls.RowFocus.GetShown(row);
        var ring = style is not null ? "its FocusVisualStyle drawn by the harness"
            : byWpf ? "focus ring drawn by WPF"
            : byRow ? "the row's focus outline, shown by RowFocus"
            : row is not null ? "the row's focus outline, switched on by the harness"
            : "no FocusVisualStyle; the control's own focused trigger is what shows";
        Console.WriteLine($"  {screen}/{theme.Suffix()}: {element.GetType().Name} focused={element.IsKeyboardFocused}, {ring}");
        WindowRenderer.Save(this, Shot(screen, theme), prepare: () =>
        {
            if (row is not null && !byRow)
            {
                Controls.RowFocus.SetShown(row, true);
                return new Removal(() => Controls.RowFocus.SetShown(row, false));
            }

            if (style is null || AdornerLayer.GetAdornerLayer(element) is not { } layer)
            {
                return null;
            }

            var drawn = new FocusRingAdorner(element, style);
            layer.Add(drawn);
            layer.UpdateLayout();
            return new Removal(() => layer.Remove(drawn));
        });
    }

    /// <summary>Undoes what a render's prepare step added.</summary>
    private sealed class Removal(Action undo) : IDisposable
    {
        public void Dispose() => undo();
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

    /// <summary>
    /// The inbox rail (Spec v0.6.0 §5 S4, as built v0.6.4) in each state worth reviewing: every group with
    /// rows and a To check session open as the pane's tab, no sessions at all, a search that matches
    /// nothing, a Not confirmed session open, History's table with Bulk discard held while a session is
    /// captured, and the rail at 150%. Then keyboard focus on a rail row and on the search.
    /// </summary>
    private async Task RailAsync(ShellState state)
    {
        var earlier = _model.Rail.Groups.Single(g => g.Collapsible);

        async Task EachThemeAsync(string screen)
        {
            await ThumbnailsAsync();
            foreach (var theme in RenderThemes.All)
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await SettleAsync();
                UpdateLayout();
                Save(Shot(screen, theme));
            }
        }

        state.OpenSession("s-1");
        earlier.IsExpanded = true;
        await EachThemeAsync("shell-rail-groups");
        earlier.IsExpanded = false;

        state.OpenSession("s-4");
        await EachThemeAsync("shell-rail-not-confirmed");
        await ThumbnailsAsync();
        foreach (var theme in new[] { RenderTheme.Dark, RenderTheme.Light })
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            await SettleAsync();
            WindowRenderer.Save(this, Shot("shell-rail-150pct", theme), scale: 1.5);
        }

        _model.Rail.Query = "anydesk";
        await EachThemeAsync("shell-rail-no-match");
        _model.Rail.Query = string.Empty;

        var rows = _sampleRows;
        _sampleRows = [];
        await _model.Rail.RefreshAsync();
        state.Navigate(ShellView.Review);
        state.OpenSession("preview");
        await EachThemeAsync("shell-rail-empty");

        // All sessions with none: the sentence as the screen's content, in place of the table.
        state.Navigate(ShellView.History);
        for (var waited = 0; waited < 2000 && _model.Content is not History.HistoryViewModel { Rows.Count: 0, Status: Core.History.SessionHistory.Empty }; waited += 50)
        {
            await SettleAsync();
            await Task.Delay(50).ConfigureAwait(true);
        }

        if (_model.Content is not History.HistoryViewModel { Rows.Count: 0, Status: Core.History.SessionHistory.Empty })
        {
            throw new InvalidOperationException("All sessions did not come up empty.");
        }

        await EachThemeAsync("shell-history-empty");
        state.Navigate(ShellView.Review);
        _sampleRows = rows;
        await _model.Rail.RefreshAsync();

        // History's table with two sessions selected and Bulk discard pressed while the sample session
        // records: nothing is asked, and the reason is said beside the button (Spec §4).
        state.Navigate(ShellView.History);
        DataGrid? table = null;
        for (var waited = 0; waited < 2000 && table is not { Items.Count: > 1 }; waited += 50)
        {
            await SettleAsync();
            _ = WindowRenderer.Lay(this);
            table = Find(this, e => e is DataGrid) as DataGrid;
            if (table is not { Items.Count: > 1 })
            {
                await Task.Delay(50).ConfigureAwait(true);
            }
        }

        if (table is null || _model.Content is not History.HistoryViewModel history)
        {
            throw new InvalidOperationException("History has no table to select in.");
        }

        table.SelectedItems.Clear();
        table.SelectedItems.Add(table.Items[0]);
        table.SelectedItems.Add(table.Items[1]);
        await history.BulkDiscardCommand.ExecuteAsync(null);
        if (history.DiscardError != SessionDiscard.WhileCapturing)
        {
            throw new InvalidOperationException($"Bulk discard while capturing said \"{history.DiscardError}\".");
        }

        await EachThemeAsync("shell-history-discard-blocked");
        table.SelectedItems.Clear();

        // Keyboard focus on the open row (the tab) and on the search.
        state.OpenSession("s-1");
        await ThumbnailsAsync();
        foreach (var theme in RenderThemes.All)
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            await SettleAsync();
            _ = WindowRenderer.Lay(this);
            await FocusAndSaveAsync(Find(Rail, e => e is ListBoxItem { IsSelected: true } && Usable(e)), "focus-rail-row", theme);
            await FocusAndSaveAsync(Find(Rail, e => e is TextBox && Usable(e)), "focus-rail-search", theme);
        }

        Keyboard.ClearFocus();
    }

    /// <summary>
    /// Spec v0.6.9 §5 S4 in a small window: the shell at its 1024 by 680 minimum, where the rail is
    /// <c>layout.rail-width-narrow</c>, with the rail's labelled rows (a long company ends in an ellipsis)
    /// and Review beside it, then All sessions with the badges, and both again at 150%, in all four themes.
    /// Then All sessions at the full window with a filter on, and with filters that leave nothing.
    /// </summary>
    private async Task SmallWindowAsync(ShellState state)
    {
        var earlier = _model.Rail.Groups.Single(g => g.Collapsible);

        async Task EachThemeAsync(string screen, double scale = 1)
        {
            await ThumbnailsAsync();
            foreach (var theme in RenderThemes.All)
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await SettleAsync();
                UpdateLayout();
                WindowRenderer.Save(this, Shot(screen, theme), scale: scale);
            }
        }

        var (width, height) = (Width, Height);
        Width = MinWidth;
        Height = MinHeight;
        _ = WindowRenderer.Intend(this);
        try
        {
            state.Navigate(ShellView.Review);
            state.OpenSession("s-1");
            earlier.IsExpanded = true;
            await SettleAsync();
            Console.WriteLine($"  rail at the minimum: {RailFrame.ActualWidth} px");
            await EachThemeAsync("shell-rail-narrow");
            await EachThemeAsync("shell-rail-narrow-150pct", 1.5);

            state.Navigate(ShellView.History);
            await HistoryRowsAsync(2);
            await EachThemeAsync("shell-history-narrow");
            await EachThemeAsync("shell-history-narrow-150pct", 1.5);
        }
        finally
        {
            earlier.IsExpanded = false;
            Width = width;
            Height = height;
            _ = WindowRenderer.Intend(this);
            await SettleAsync();
        }

        // All sessions with one filter on (published only), then with filters that leave nothing.
        state.Navigate(ShellView.History);
        await HistoryRowsAsync(2);
        if (_model.Content is not History.HistoryViewModel history)
        {
            throw new InvalidOperationException("All sessions has no model to filter.");
        }

        history.StatusChoice = Core.History.HistoryFilters.StatusChoices.Single(c => c.Value == "published");
        await SettleAsync();
        Console.WriteLine($"  filtered: {history.Status}");
        await EachThemeAsync("shell-history-filtered");

        history.ToolChoice = history.ToolChoices.Single(c => c.Value == "screenconnect");
        await SettleAsync();
        if (history.Status != Core.History.HistoryFilters.NoMatch)
        {
            throw new InvalidOperationException($"Filters that match nothing said \"{history.Status}\".");
        }

        await EachThemeAsync("shell-history-no-match");
        history.ClearFiltersCommand.Execute(null);
        await SettleAsync();
        state.Navigate(ShellView.Review);
    }

    /// <summary>Waits for All sessions' table to hold at least <paramref name="rows"/> rows.</summary>
    private async Task HistoryRowsAsync(int rows)
    {
        for (var waited = 0; waited < 2000; waited += 50)
        {
            await SettleAsync();
            _ = WindowRenderer.Lay(this);
            if (Find(this, e => e is DataGrid) is DataGrid { Items.Count: var count } && count >= rows)
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        throw new InvalidOperationException("All sessions' table did not fill.");
    }

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

    /// <summary>
    /// ST-083's wizard, every step, over the sample gateways: a binding that breaks in a step fails this
    /// harness rather than the technician's first five minutes. Each step in all four themes at the size
    /// the window opens at, then in dark and light at 150%, and in light at the window's own minimum
    /// (Spec §5 S8, as built v0.6.7), which is where a step's content is most likely to be cut off.
    /// </summary>
    private async Task OnboardingAsync()
    {
        foreach (var step in Enum.GetValues<OnboardingStep>())
        {
            var model = await SampleOnboardingAsync(step);
            var name = $"onboarding-{step.ToString().ToLowerInvariant()}";
            foreach (var theme in RenderThemes.All)
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await OnboardingShotAsync(model, Shot(name, theme));
            }

            foreach (var theme in new[] { RenderTheme.Dark, RenderTheme.Light })
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await OnboardingShotAsync(model, Shot($"{name}-150pct", theme), scale: 1.5);
            }

            RenderThemeApplier.Apply(RenderTheme.Light, Application.Current.Resources);
            await OnboardingShotAsync(model, Shot($"{name}-min", RenderTheme.Light), smallest: true);
        }
    }

    private static async Task OnboardingShotAsync(Onboarding.OnboardingViewModel model, string path, double scale = 1, bool smallest = false)
    {
        var window = new Onboarding.OnboardingWindow(model) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
        if (smallest)
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
        }

        _ = WindowRenderer.Intend(window);
        window.Show();
        await SettleAsync();
        window.UpdateLayout();
        WindowRenderer.Save(window, path, scale: scale);
        window.Close();
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

    /// <summary>
    /// The note as it will land (Spec v0.6.0 §5 S3), keyed by the session id the harness opens: every step
    /// checked (the third was inferred and confirmed), the third inferred and not checked, the second
    /// step's frame with two masked regions, no follow-ups, and a long note. Anything else is the fixture.
    /// </summary>
    internal static Shared.Schema.Session SampleSession(Shared.Schema.Session fixture, string id)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.Draft is not { } draft)
        {
            return fixture;
        }

        Shared.Schema.DraftNote Third(bool confirmed) => draft with
        {
            Steps = [.. draft.Steps.Select((step, at) => at == 2 ? step with { Confidence = Shared.Schema.StepConfidence.Low, Confirmed = confirmed ? true : null } : step)],
        };

        return id switch
        {
            "preview-note-checked" => fixture with { Draft = Third(confirmed: true) },
            "preview-note-inferred" => fixture with { Draft = Third(confirmed: false) },
            "preview-note-masked" => fixture with
            {
                Draft = Third(confirmed: true),
                Frames = [.. fixture.Frames.Select(frame => frame.Id == "f-0002" ? frame with
                {
                    MaskedRegions =
                    [
                        new Shared.Schema.MaskedRegion { X = 236, Y = 6, Width = 116, Height = 24, Kind = Shared.Schema.MaskKind.CustomPattern },
                        new Shared.Schema.MaskedRegion { X = 80, Y = 256, Width = 220, Height = 26, Kind = Shared.Schema.MaskKind.UserBlur },
                    ],
                } : frame)],
            },
            "preview-note-no-followups" => fixture with { Draft = Third(confirmed: true) with { FollowUps = [] } },
            "preview-note-long" => fixture with { Draft = LongNote(Third(confirmed: false)) },
            _ => fixture,
        };
    }

    /// <summary>
    /// The note variants save to nowhere, quietly, so their renders show the save state as the running
    /// application does. The other sessions persist nothing and say nothing (NoteEditorViewModel.SaveLabel).
    /// </summary>
    private static Func<Shared.Schema.DraftNote, CancellationToken, Task>? SampleWrite(string id) =>
        id.StartsWith("preview-note-", StringComparison.Ordinal) ? (_, _) => Task.CompletedTask : null;

    /// <summary>A note long enough to scroll at the shell's size: nine steps, some of them two lines.</summary>
    private static Shared.Schema.DraftNote LongNote(Shared.Schema.DraftNote draft)
    {
        Shared.Schema.DraftStep Plain(string text) => new() { Text = text, Confidence = Shared.Schema.StepConfidence.High, FrameRefs = [] };
        return draft with
        {
            Problem = draft.Problem + " The front desk had restarted the PC twice before calling, and the queue showed four jobs from the morning stuck as Error - Printing.",
            Steps =
            [
                .. draft.Steps,
                Plain("Checked the HP driver version against the one the other front-desk machines use; this one was a year older."),
                Plain("Set the Print Spooler recovery options to restart the service on the first and second failure."),
                Plain("Asked the receptionist to print the day's schedule from the practice software, which came out first time."),
                Plain("Left a note on the workstation's asset record about the older driver."),
                Plain("Confirmed with the office manager that printing works from both reception PCs."),
            ],
            FollowUps = [.. draft.FollowUps, "Schedule the HP driver update for this machine after hours.", "Check the other reception PC's spooler recovery settings."],
        };
    }

    /// <summary>
    /// The note's states, each in every theme: checked steps, an inferred step not checked, a masked frame,
    /// an empty Follow-ups, a long note scrolled to its middle, the inferred note at 150%, and keyboard
    /// focus on Looks right.
    /// </summary>
    private async Task NoteAsync(ShellState state)
    {
        // What the prepares search: a window smaller than the render lends its content to a host for the
        // picture (WindowRenderer.RenderDetached), and a search from the window then finds nothing.
        var content = (DependencyObject)Content;

        // Each is scrolled to what it is for: the window shows two steps at most, and a render of the
        // inferred note that shows only step 1 shows nothing inferred. Null is the end of the note, where
        // the empty Follow-ups is.
        foreach (var (name, session, step) in new (string, string, int?)[]
        {
            ("checked", "preview-note-checked", 2),
            ("inferred", "preview-note-inferred", 2),
            ("masked", "preview-note-masked", 1),
            ("no-followups", "preview-note-no-followups", null),
        })
        {
            state.OpenSession(session);
            await ThumbnailsAsync();
            foreach (var theme in RenderThemes.All)
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await SettleAsync();
                WindowRenderer.Save(this, Shot($"shell-review-{name}", theme), prepare: () => ScrollNoteTo(content, step));
            }
        }

        // Del on the first step's screenshot (Spec v0.6.8): the screenshot is off its row and the undo is at
        // the foot of the note. Deleted and undone in each theme, so the five seconds never run out mid-render.
        state.OpenSession("preview-note-checked");
        await ThumbnailsAsync();
        if (_model.Content is ReviewViewModel { Note: { } deleting } && deleting.Steps.SelectMany(s => s.Frames).FirstOrDefault(f => f.Present) is { } first)
        {
            foreach (var theme in RenderThemes.All)
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await deleting.DeleteFrameCommand.ExecuteAsync(first);
                await SettleAsync();
                WindowRenderer.Save(this, Shot("shell-review-step-deleted", theme), prepare: () => ScrollNoteTo(content, 0));
                await deleting.UndoDeleteCommand.ExecuteAsync(null);
                await SettleAsync();
            }
        }

        // Long, scrolled: the scroll happens once the note is laid out at the render's size.
        state.OpenSession("preview-note-long");
        await ThumbnailsAsync();
        foreach (var theme in RenderThemes.All)
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            await SettleAsync();
            WindowRenderer.Save(this, Shot("shell-review-long-scrolled", theme), prepare: () =>
            {
                if (Find(content, e => e is ScrollViewer { Name: "NoteScroll" }) is ScrollViewer note)
                {
                    note.ScrollToVerticalOffset(note.ScrollableHeight * 0.55);
                    note.UpdateLayout();
                }

                return null;
            });
        }

        // The inferred note at 150%, in dark and light.
        state.OpenSession("preview-note-inferred");
        await ThumbnailsAsync();
        foreach (var theme in new[] { RenderTheme.Dark, RenderTheme.Light })
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            await SettleAsync();
            WindowRenderer.Save(this, Shot("shell-review-inferred-150pct", theme), scale: 1.5, prepare: () => ScrollNoteTo(content, 2));
        }

        // Keyboard focus on Looks right, where Tab takes a technician after the inferred step's text.
        foreach (var theme in RenderThemes.All)
        {
            RenderThemeApplier.Apply(theme, Application.Current.Resources);
            await SettleAsync();
            _ = WindowRenderer.Lay(this);
            var looksRight = Find(this, e => e is Button button && Usable(e) && AutomationProperties.GetName(button).StartsWith("Looks right", StringComparison.Ordinal));
            await FocusAndSaveAsync(looksRight, "focus-looks-right", theme);
        }

        Keyboard.ClearFocus();
    }

    /// <summary>
    /// The note scrolled so step <paramref name="step"/> (from 0) is at the top of its view, or to its end
    /// when null. For a render's prepare, once the note is laid out at the render's size.
    /// </summary>
    private static IDisposable? ScrollNoteTo(DependencyObject content, int? step)
    {
        if (Find(content, e => e is ScrollViewer { Name: "NoteScroll" }) is not ScrollViewer note)
        {
            return null;
        }

        if (step is not { } index)
        {
            note.ScrollToBottom();
        }
        else if (Find(note, e => e is ItemsControl { Name: "StepList" }) is ItemsControl steps
            && index < steps.Items.Count
            && steps.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row)
        {
            var top = row.TransformToAncestor(note).Transform(default).Y;
            note.ScrollToVerticalOffset(Math.Max(0, note.VerticalOffset + top - 8));
        }

        note.UpdateLayout();
        return null;
    }

    /// <summary>
    /// Every question the app's one dialog asks (v0.6.11): the typed ones (Delete everything, Discard,
    /// bulk discard, before and after the count is typed), the plain ones (Mark as published, Quit while
    /// capturing) and the notice; then the shortcut sheet and <paramref name="others"/> (the diagnostics
    /// panel, whose sample only ShellWindow.xaml.cs may build: ReleaseSurfaceTests). Each in all four
    /// themes, and in dark and light at 150%.
    /// </summary>
    private async Task DialogsAsync(params (string Name, Func<Window> Build)[] others)
    {
        foreach (var (name, build) in new (string, Func<Window>)[]
        {
            ("dialog-delete-everything", () => new ConfirmWindow(ConfirmCopy.DeleteEverything, "DELETE EVERYTHING")),
            ("dialog-discard-session", () => new ConfirmWindow(ConfirmCopy.DiscardSession, TypedConfirmation.ForDiscard())),
            ("dialog-bulk-discard", () => new ConfirmWindow(ConfirmCopy.DiscardSessions(3), TypedConfirmation.ForCount(3))),
            ("dialog-bulk-discard-typed", () => Typed(new ConfirmWindow(ConfirmCopy.DiscardSessions(3), TypedConfirmation.ForCount(3)), "3")),
            ("dialog-mark-published", () => new ConfirmWindow(ConfirmCopy.MarkAsPublished("ScreenConnect session, started Mon 14 Sep 16:40, 9 min"))),
            ("dialog-quit-capturing", () => new ConfirmWindow(ConfirmCopy.QuitWhileCapturing)),
            ("dialog-not-discarded", () => new ConfirmWindow(ConfirmCopy.NotDiscarded(ConfirmedAction.NotConnected))),
            ("shortcut-sheet", () => new ShortcutSheet()),
        }.Concat(others))
        {
            foreach (var theme in RenderThemes.All)
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await ShotOfAsync(build(), Shot(name, theme), scale: 1);
            }

            foreach (var theme in new[] { RenderTheme.Dark, RenderTheme.Light })
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await ShotOfAsync(build(), Shot($"{name}-150pct", theme), scale: 1.5);
            }
        }

        static ConfirmWindow Typed(ConfirmWindow dialog, string text)
        {
            dialog.Typed.Text = text;
            return dialog;
        }
    }

    /// <summary>A window of its own, shown off the screen, drawn at <paramref name="scale"/> and closed.</summary>
    private static async Task ShotOfAsync(Window window, string path, double scale)
    {
        var shown = WindowRenderer.Intend(window);
        shown.ShowInTaskbar = false;
        shown.WindowStartupLocation = WindowStartupLocation.Manual;
        shown.Left = -4000;
        shown.Top = -4000;
        shown.Show();
        await SettleAsync();
        shown.UpdateLayout();
        WindowRenderer.Save(shown, path, scale: scale);
        shown.Close();
    }

    /// <summary>
    /// Takes the mouse off the shell before the renders, so they show every control at rest. On the hosted
    /// runner the pointer sits where the desktop put it, over the middle of the window, and Settings drew
    /// its Integrations nav item hovered in every render. Where it was, and over what kind of element, is
    /// written to the log first (types and names only, never text: INV-10), so a hover that is a real
    /// defect rather than the pointer's place can still be told apart.
    /// </summary>
    private void RestTheMouse()
    {
        var at = Mouse.GetPosition(this);
        var over = Mouse.DirectlyOver is FrameworkElement element
            ? $"{element.GetType().Name} '{element.Name}'" + (Ancestor<ListBoxItem>(element) is { } item ? $" in a {item.GetType().Name}" : string.Empty)
            : Mouse.DirectlyOver?.GetType().Name ?? "nothing";
        Console.WriteLine($"  the mouse was at {at.X:F0},{at.Y:F0} in the shell, over {over}; renders draw controls at rest");
        if (Content is UIElement root)
        {
            root.IsHitTestVisible = false;
        }

        Mouse.Synchronize();
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
