using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ScreenTail.UI.Review;
using ScreenTail.UI.Review.Publish;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// The shortcuts Spec §4 and §5 S3 document, pressed through WPF's own keyboard pipeline on the real
/// windows (<see cref="Keys"/>), and what each one has to do. Esc closes every dialog it says it closes.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class KeyboardShortcutTests(TheApplication app)
{
    [Fact]
    public async Task ReviewsChordsDoWhatTheSpecSays()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review");
            try
            {
                var review = Screens.Review(shell);
                var pane = Audit.Descendants(shell).OfType<ReviewPane>().Single();
                var problem = Screens.Named<TextBox>(shell, "Problem");
                _ = problem.Focus();
                await Audit.Settle();

                // Ctrl+S: save now. The sample saves nowhere, so what is held is that the chord reaches the
                // note's save command, and nothing else takes it first.
                var save = pane.InputBindings.OfType<KeyBinding>().SingleOrDefault(b => b.Gesture is KeyGesture { Key: Key.S, Modifiers: ModifierKeys.Control });
                Assert.True(save is not null, "Ctrl+S is not bound in Review.");
                Assert.Same(review.Note.SaveNowCommand, save.Command);
                Assert.True(Keys.Press(shell, Key.S, ModifierKeys.Control), "Ctrl+S in the note was not handled.");

                // Alt+T: the timeline opens and closes.
                var expanded = review.Timeline.Expanded;
                _ = Keys.Press(shell, Key.T, ModifierKeys.Alt);
                await Audit.Settle();
                Assert.NotEqual(expanded, review.Timeline.Expanded);
                _ = Keys.Press(shell, Key.T, ModifierKeys.Alt);
                await Audit.Settle();
                Assert.Equal(expanded, review.Timeline.Expanded);

                // Alt+1, Alt+2, Alt+3: focus goes into the note, the screenshots and the publish pane.
                foreach (var (key, view) in new (Key, FrameworkElement)[]
                {
                    (Key.D2, Audit.Descendants(shell).OfType<FilmstripView>().Single()),
                    (Key.D3, Audit.Descendants(shell).OfType<PublishPane>().Single()),
                    (Key.D1, Audit.Descendants(shell).OfType<NoteEditorView>().Single()),
                })
                {
                    _ = Keys.Press(shell, key, ModifierKeys.Alt);
                    await Audit.Settle();
                    Assert.True(Screens.Within(Keyboard.FocusedElement, view), $"Alt+{key.ToString()[1..]} did not put focus in {view.GetType().Name}.");
                }

                // Alt+C confirms the step (the fixture's steps are all confirmed already, so what is held here
                // is that the chord reaches the step box's handler and leaves the step confirmed; Core's tests
                // hold what confirming does). Alt+Down moves a step down.
                var step = review.Note.Steps[^1];
                _ = StepBox(shell, step).Focus();
                await Audit.Settle();
                Assert.True(Keys.Press(shell, Key.C, ModifierKeys.Alt), "Alt+C in a step was not handled.");
                await Audit.Settle();
                Assert.False(step.NeedsVerification, "Alt+C left the step unconfirmed.");

                var first = review.Note.Steps[0];
                _ = StepBox(shell, first).Focus();
                await Audit.Settle();
                _ = Keys.Press(shell, Key.Down, ModifierKeys.Alt);
                await Audit.Settle();
                Assert.Same(first, review.Note.Steps[1]);

                // The screenshots: Space, Enter, T, Esc, Del and Ctrl+Z.
                var item = await Screens.FocusFirstFrameAsync(shell);
                var frame = (FrameItem)item.DataContext;
                var included = frame.Included;
                _ = Keys.Press(shell, Key.Space);
                await Audit.Settle();
                Assert.NotEqual(included, frame.Included);
                _ = Keys.Press(shell, Key.Space);
                await Audit.Settle();
                Assert.Equal(included, frame.Included);

                _ = Keys.Press(shell, Key.Enter);
                await Audit.Settle();
                Assert.True(review.Strip.IsEnlarged, "Enter did not enlarge the screenshot.");
                if (review.Strip.Enlarged!.HasOcrText)
                {
                    _ = Keys.Press(shell, Key.T);
                    await Audit.Settle();
                    Assert.True(review.Strip.ShowOcrText, "T did not show the text read off the screenshot.");
                }

                _ = Keys.Press(shell, Key.Escape);
                await Audit.Settle();
                Assert.False(review.Strip.IsEnlarged, "Esc did not close the enlarged screenshot.");

                // Esc closes it from its own buttons too, which is where Tab takes a keyboard user.
                item = await Screens.FocusFirstFrameAsync(shell);
                _ = Keys.Press(shell, Key.Enter);
                await Audit.Settle();
                var close = Audit.Descendants(shell).OfType<Button>().Single(b => b.Command == review.Strip.CloseEnlargedCommand);
                _ = close.Focus();
                await Audit.Settle();
                _ = Keys.Press(shell, Key.Escape);
                await Audit.Settle();
                Assert.False(review.Strip.IsEnlarged, "Esc on the enlarged screenshot's Close button did not close it.");
                Assert.True(Screens.Within(Keyboard.FocusedElement, Audit.Descendants(shell).OfType<FilmstripView>().Single()), "Closing the enlarged screenshot left focus outside the screenshots.");

                item = await Screens.FocusFirstFrameAsync(shell);
                var count = review.Strip.Items.Count;
                _ = Keys.Press(shell, Key.Delete);
                await Screens.UntilAsync(() => review.Strip.Items.Count == count - 1);
                Assert.Equal(count - 1, review.Strip.Items.Count);
                Assert.True(review.Strip.HasUndoToast, "Del did not offer an undo.");
                _ = Keys.Press(shell, Key.Z, ModifierKeys.Control);
                await Screens.UntilAsync(() => review.Strip.Items.Count == count);
                Assert.Equal(count, review.Strip.Items.Count);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    /// <summary>
    /// Alt+1/2/3 is the digit row only. With the number pad, Alt types a character by its code (Alt+0233
    /// is é), so a technician typing one must keep their place (ST-084 review).
    /// </summary>
    [Fact]
    public async Task AltWithTheNumberPadIsLeftForTyping()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review");
            try
            {
                var minutes = Screens.Named<TextBox>(shell, "Minutes");
                foreach (var key in new[] { Key.NumPad1, Key.NumPad2, Key.NumPad3 })
                {
                    _ = minutes.Focus();
                    await Audit.Settle();
                    _ = Keys.Press(shell, key, ModifierKeys.Alt);
                    await Audit.Settle();
                    Assert.True(ReferenceEquals(Keyboard.FocusedElement, minutes), $"Alt+{key} moved focus out of the box being typed in.");
                }

                // The control: the digit row does move it.
                _ = Keys.Press(shell, Key.D1, ModifierKeys.Alt);
                await Audit.Settle();
                Assert.True(Screens.Within(Keyboard.FocusedElement, Audit.Descendants(shell).OfType<NoteEditorView>().Single()), "Alt+1 did not go to the note.");
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    /// <summary>
    /// A delete is undone with Ctrl+Z for five seconds (Spec §4), and enlarging another screenshot in that
    /// time hides the toast but not the undo (ST-084 review).
    /// </summary>
    [Fact]
    public async Task CtrlZUndoesADeleteFromTheEnlargedView()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review");
            try
            {
                var strip = Screens.Review(shell).Strip;
                var count = strip.Items.Count;
                _ = await Screens.FocusFirstFrameAsync(shell);
                _ = Keys.Press(shell, Key.Delete);
                await Screens.UntilAsync(() => strip.Items.Count == count - 1);
                Assert.Equal(count - 1, strip.Items.Count);

                await Screens.EnlargeFirstFrameAsync(shell);
                Assert.True(strip.HasUndoToast, "The undo window closed before the test could use it.");
                Assert.True(Screens.Within(Keyboard.FocusedElement, Audit.Descendants(shell).OfType<FilmstripView>().Single()), "Focus left the screenshots when the frame was enlarged.");
                _ = Keys.Press(shell, Key.Z, ModifierKeys.Control);
                await Screens.UntilAsync(() => strip.Items.Count == count);
                Assert.Equal(count, strip.Items.Count);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task CtrlEnterPublishes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review");
            try
            {
                var review = Screens.Review(shell);
                Assert.True(review.Publish.CanPublish, "The sample cannot publish.");
                _ = Screens.Named<TextBox>(shell, "Problem").Focus();
                await Audit.Settle();
                _ = Keys.Press(shell, Key.Enter, ModifierKeys.Control);
                await Screens.UntilAsync(() => review.Publish.ResultsVisibility == Visibility.Visible);
                Assert.Equal(Visibility.Visible, review.Publish.ResultsVisibility);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    /// <summary>
    /// Spec §5 S3: a disabled Publish says why. The reason is written beside it, and is also the button's
    /// help text, so a screen reader that reaches the button hears it (a disabled button takes no focus).
    /// </summary>
    [Fact]
    public async Task ADisabledPublishSaysWhyToAScreenReader()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review");
            try
            {
                var publish = Screens.Review(shell).Publish;
                publish.TicketNote = false;
                publish.TimeEntry = false;
                publish.KbArticle = false;
                await Audit.Settle();
                Assert.False(publish.CanPublish);
                Assert.False(string.IsNullOrEmpty(publish.BlockedBecause));
                var button = Audit.Descendants(shell).OfType<Button>().Single(b => b.Command == publish.PublishCommand && b.IsVisible);
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(button);
                Assert.Equal(publish.BlockedBecause, peer.GetHelpText());
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    /// <summary>Spec §4: ? opens the shortcut sheet from any pane, Esc closes it, and in a text box ? is typed.</summary>
    [Fact]
    public async Task QuestionMarkOpensTheShortcutSheetAndEscClosesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("history");
            try
            {
                _ = Screens.Named<Button>(shell, "History").Focus();
                await Audit.Settle();
                Keys.Type("?");
                await Audit.Settle();
                var sheet = shell.OwnedWindows.OfType<ShortcutSheet>().SingleOrDefault();
                Assert.True(sheet is not null, "? did not open the shortcut sheet.");
                _ = sheet.Activate();
                await Audit.Settle();
                Assert.True(Screens.Within(Keyboard.FocusedElement, sheet), "The sheet opened without focus.");
                _ = Keys.Press(sheet, Key.Escape);
                await Audit.Settle();
                Assert.Empty(shell.OwnedWindows.OfType<ShortcutSheet>());
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }

            var review = await Screens.OpenAsync("review");
            try
            {
                var problem = Screens.Named<TextBox>(review, "Problem");
                _ = problem.Focus();
                problem.CaretIndex = problem.Text.Length;
                await Audit.Settle();
                var before = problem.Text;
                Keys.Type("?");
                await Audit.Settle();
                Assert.Empty(review.OwnedWindows.OfType<ShortcutSheet>());
                Assert.Equal(before + "?", problem.Text);
            }
            finally
            {
                await Screens.CloseAsync(review);
            }
        });
    }

    /// <summary>
    /// The typed confirmations (Spec §3): the caret is in the box when they open, and Esc keeps the data
    /// and closes them. The shortcut sheet closes on Esc too.
    /// </summary>
    [Theory]
    [InlineData("confirm-discard")]
    [InlineData("confirm-bulk-discard")]
    [InlineData("confirm-erase")]
    [InlineData("shortcut-sheet")]
    public async Task EscClosesTheDialog(string screen)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var dialog = await Screens.OpenAsync(screen);
            try
            {
                if (screen != "shortcut-sheet")
                {
                    Assert.True(Keyboard.FocusedElement is TextBox, $"{screen} opened with focus on {Keyboard.FocusedElement?.GetType().Name ?? "nothing"}, not in the box to type into.");
                }

                _ = Keys.Press(dialog, Key.Escape);
                await Screens.UntilAsync(() => !dialog.IsVisible);
                Assert.False(dialog.IsVisible, $"Esc did not close {screen}.");
            }
            finally
            {
                await Screens.CloseAsync(dialog);
            }
        });
    }

    private static TextBox StepBox(Window shell, StepRow row) =>
        Audit.Descendants(shell).OfType<TextBox>().First(b => ReferenceEquals(b.DataContext, row));
}
