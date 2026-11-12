using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenTail.Core.Onboarding;
using ScreenTail.Core.Review;
using ScreenTail.Core.Shell;
using ScreenTail.UI.Review;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// Every screen a technician reaches (Spec §5 S1 to S8), opened with the screenshot harness's sample data
/// so the suite sees what the gallery job renders: Review in each publish state and with the timeline
/// open, a frame enlarged, or the blur box out over it, History, Settings with its sections, the service-down banner, each of the
/// seven onboarding steps, the diagnostics panel answered and unanswered, the typed confirmations and the
/// shortcut sheet. The pill and the tray menu have their own tests: neither takes keyboard focus.
///
/// Windows are shown at the shell's minimum size (1024 x 680) at the top left, which fits the hosted
/// runner's 1024 x 768 screen, and are the tightest layout a technician can have.
/// </summary>
internal static class Screens
{
    public static readonly string[] All =
    [
        "review", "review-no-psa", "review-published", "review-partial", "review-needs-mapping", "review-timeline", "review-enlarged",
        "review-blur-box", "review-note-inferred", "review-note-masked", "review-strip", "review-step-enlarged", "history", "settings", "service-down",
        "onboarding-welcome", "onboarding-activate", "onboarding-permissions", "onboarding-tools", "onboarding-connect", "onboarding-tryit", "onboarding-done",
        "diagnostics", "diagnostics-unanswered",
        "confirm-discard", "confirm-bulk-discard", "confirm-erase", "shortcut-sheet",
    ];

    /// <summary>Opens <paramref name="screen"/> on the application's thread and waits until it has drawn.</summary>
    public static async Task<Window> OpenAsync(string screen)
    {
        Window window;
        switch (screen)
        {
            case var review when review.StartsWith("review", StringComparison.Ordinal):
                var enlarged = review is "review-enlarged" or "review-blur-box";
                var sample = enlarged || review is "review-strip" or "review-step-enlarged" ? "preview" : review.Replace("review", "preview", StringComparison.Ordinal);
                window = await ShellAsync(state => state.OpenSession(sample));
                await PaneAsync<ReviewPane>(window);
                if (enlarged)
                {
                    await EnlargeFirstFrameAsync(window);
                }

                if (review == "review-blur-box")
                {
                    _ = await OpenBlurBoxAsync(window);
                }

                // The whole strip over the note (Alt+2), and a step's own frame enlarged over it.
                if (review == "review-strip")
                {
                    _ = await FocusFirstFrameAsync(window);
                }

                if (review == "review-step-enlarged")
                {
                    await EnlargeFirstStepsFrameAsync(window);
                }

                break;
            case "history":
                window = await ShellAsync(state => state.Navigate(ShellView.History));
                await PaneAsync<UI.History.HistoryPane>(window);
                break;
            case "settings":
                window = await ShellAsync(state => state.Navigate(ShellView.Settings));
                await PaneAsync<UI.Settings.SettingsPane>(window);
                break;
            case "service-down":
                window = await ShellAsync(state =>
                {
                    state.Navigate(ShellView.Review);
                    state.Lost();
                });
                break;
            case var step when step.StartsWith("onboarding-", StringComparison.Ordinal):
                var which = Enum.GetValues<OnboardingStep>().Single(s => step == "onboarding-" + s.ToString().ToLowerInvariant());
                window = await ShowAsync(new UI.Onboarding.OnboardingWindow(await ShellWindow.SampleOnboardingAsync(which)));
                break;
            case "diagnostics":
                window = await ShowAsync(new UI.Diagnostics.DiagnosticsWindow());
                break;
            case "diagnostics-unanswered":
                window = await ShowAsync(new UI.Diagnostics.DiagnosticsWindow(DiagnosticsPanel.Unanswered));
                break;
            case "confirm-discard":
                window = await ShowModalAsync(new ConfirmTypedDialog(
                    TypedConfirmation.ForDiscard(),
                    "Discard this session?",
                    "The screenshots, transcript and timeline are deleted from this device straight away, and the note goes with them. This cannot be undone."));
                break;
            case "confirm-bulk-discard":
                window = await ShowModalAsync(new ConfirmTypedDialog(
                    TypedConfirmation.ForCount(3),
                    "Discard 3 sessions?",
                    "Their screenshots, transcripts and timelines are deleted from this device straight away, and their notes go with them. This cannot be undone.",
                    "Discard 3 sessions"));
                break;
            case "confirm-erase":
                window = await ShowModalAsync(new ConfirmWindow(
                    "Delete everything on this device",
                    "Every session, draft, screenshot and transcript, your settings and this device's tokens are removed. This cannot be undone.",
                    "DELETE EVERYTHING"));
                break;
            case "shortcut-sheet":
                window = await ShowAsync(new ShortcutSheet());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(screen), screen, "No such screen.");
        }

        return window;
    }

    /// <summary>Closes what <see cref="OpenAsync"/> opened, ending a modal loop if it is one.</summary>
    public static async Task CloseAsync(Window window)
    {
        if (window.IsLoaded)
        {
            window.Close();
        }

        await Audit.Settle();
    }

    /// <summary>The sample shell (the screenshot harness's), arranged by <paramref name="arrange"/>.</summary>
    public static async Task<ShellWindow> ShellAsync(Action<ShellState> arrange)
    {
        var shell = new ShellWindow();
        arrange(shell.Sample!);
        _ = await ShowAsync(shell, 1024, 680);
        return shell;
    }

    public static async Task<Window> ShowAsync(Window window, double? width = null, double? height = null)
    {
        Place(window, width, height);
        window.Show();
        _ = window.Activate();
        await DrawnAsync(window);
        return window;
    }

    /// <summary>
    /// Shows a dialog the way the app does, modally, from the dispatcher, and returns once it has drawn:
    /// the caller carries on inside the dialog's own message loop until it closes.
    /// </summary>
    public static async Task<Window> ShowModalAsync(Window dialog)
    {
        Place(dialog, null, null);
        _ = dialog.Dispatcher.BeginInvoke(() => dialog.ShowDialog());
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!dialog.IsLoaded && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }

        Assert.True(dialog.IsLoaded, $"{dialog.GetType().Name} did not open.");
        _ = dialog.Activate();
        await DrawnAsync(dialog);
        return dialog;
    }

    private static void Place(Window window, double? width, double? height)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;
        window.ShowInTaskbar = false;
        if (width is { } w)
        {
            window.Width = w;
        }

        if (height is { } h)
        {
            window.Height = h;
        }
    }

    private static async Task DrawnAsync(Window window)
    {
        for (var i = 0; i < 5; i++)
        {
            await Audit.Settle();
        }

        window.UpdateLayout();
    }

    /// <summary>Waits until the shell's content area holds a loaded <typeparamref name="T"/>.</summary>
    public static async Task<T> PaneAsync<T>(Window window)
        where T : FrameworkElement
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (Audit.Descendants(window).OfType<T>().FirstOrDefault(p => p.IsLoaded && p.IsVisible) is { } pane)
            {
                await DrawnAsync(window);
                return pane;
            }

            await Task.Delay(20);
            await Audit.Settle();
        }

        throw new InvalidOperationException($"The shell never showed a {typeof(T).Name}.");
    }

    /// <summary>The Review pane's view model, behind the shell's content.</summary>
    public static ReviewViewModel Review(Window shell) =>
        Audit.Descendants(shell).OfType<ReviewPane>().Single().DataContext as ReviewViewModel
        ?? throw new InvalidOperationException("The Review pane has no view model.");

    /// <summary>
    /// Puts keyboard focus on the strip's first screenshot, as a click or the arrows would. In Review the
    /// strip opens over the note (Spec v0.6.0 §5 S3, as built v0.6.5), so it is opened first with Alt+2,
    /// the way a technician gets there.
    /// </summary>
    public static async Task<ListBoxItem> FocusFirstFrameAsync(Window shell)
    {
        var strip = Audit.Descendants(shell).OfType<FilmstripView>().Single();
        if (!strip.IsVisible)
        {
            // Alt+2 is the Review pane's own chord, so it is pressed from inside the pane, from the note,
            // where a technician who has just opened the session is. A freshly opened shell has focus
            // elsewhere (or nowhere), and the press would never reach the pane.
            var pane = Audit.Descendants(shell).OfType<ReviewPane>().Single();
            if (!pane.IsKeyboardFocusWithin)
            {
                _ = Audit.Descendants(shell).OfType<NoteEditorView>().Single().MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                await Audit.Settle();
                Assert.True(pane.IsKeyboardFocusWithin, "Focus did not go into the note.");
            }

            _ = Keys.Press(shell, Key.D2, ModifierKeys.Alt);
            await UntilAsync(() => strip.IsVisible);
            Assert.True(strip.IsVisible, "Alt+2 did not open the screenshots over the note.");
        }

        var item = Audit.Descendants(strip).OfType<ListBoxItem>().First(i => i.DataContext is FrameItem);
        _ = item.Focus();
        item.IsSelected = true;
        await Audit.Settle();
        Assert.True(item.IsKeyboardFocused, "The first screenshot did not take keyboard focus.");
        return item;
    }

    /// <summary>Enter on the first screenshot: the enlarged view (Spec §5 S3).</summary>
    public static async Task EnlargeFirstFrameAsync(Window shell)
    {
        _ = await FocusFirstFrameAsync(shell);
        _ = Keys.Press(shell, Key.Enter);

        // Enlarging reads the frame's picture and text first, so the view opens a moment after Enter,
        // however long a busy runner takes over it.
        await UntilAsync(() => Review(shell).Strip.IsEnlarged);
        await DrawnAsync(shell);
        Assert.True(Review(shell).Strip.IsEnlarged, "Enter on a screenshot did not enlarge it.");
    }

    /// <summary>The first step's frame, pressed with Enter on its row: enlarged over the note (Spec v0.6.0 §5 S3).</summary>
    public static async Task<Button> EnlargeFirstStepsFrameAsync(Window shell)
    {
        var note = Audit.Descendants(shell).OfType<NoteEditorView>().Single();
        var frame = Audit.Descendants(note).OfType<Button>().First(b => b.DataContext is FrameChip && b.IsVisible);
        _ = frame.Focus();
        await Audit.Settle();
        Assert.True(frame.IsKeyboardFocused, "The first step's frame did not take keyboard focus.");
        _ = Keys.Press(shell, Key.Enter);
        await UntilAsync(() => Review(shell).Strip.IsEnlarged);
        await DrawnAsync(shell);
        Assert.True(Review(shell).Strip.IsEnlarged, "Enter on a step's frame did not enlarge it.");
        return frame;
    }

    /// <summary>`B` on the enlarged screenshot: the blur box out, in the middle of the picture, with focus on it.</summary>
    public static async Task<BlurBoxControl> OpenBlurBoxAsync(Window shell)
    {
        var box = Audit.Descendants(shell).OfType<BlurBoxControl>().Single();
        _ = Keys.Press(shell, Key.B);
        await UntilAsync(() => box.IsVisible && box.IsKeyboardFocused);
        await DrawnAsync(shell);
        Assert.True(box.IsVisible, "B on the enlarged screenshot did not bring out the blur box.");
        Assert.True(box.IsKeyboardFocused, $"B brought out the blur box with focus on {Keyboard.FocusedElement?.GetType().Name ?? "nothing"}, not on the box.");
        return box;
    }

    /// <summary>The element of type <typeparamref name="T"/> whose automation name is <paramref name="name"/>.</summary>
    public static T Named<T>(DependencyObject root, string name)
        where T : FrameworkElement =>
        Audit.Descendants(root).OfType<T>().FirstOrDefault(e => System.Windows.Automation.AutomationProperties.GetName(e) == name || (e is ContentControl { Content: string text } && text == name))
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named \"{name}\".");

    /// <summary>Lets the dispatcher run until <paramref name="done"/> holds, five seconds at most.</summary>
    public static async Task UntilAsync(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
            await Audit.Settle();
        }
    }

    /// <summary>Whether <paramref name="element"/> is inside <paramref name="container"/>.</summary>
    public static bool Within(IInputElement? element, Visual container) =>
        element is Visual visual && (ReferenceEquals(visual, container) || visual.IsDescendantOf(container));
}
