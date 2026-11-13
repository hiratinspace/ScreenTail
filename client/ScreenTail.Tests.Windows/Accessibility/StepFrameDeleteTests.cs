using System.Runtime.Versioning;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ScreenTail.UI.Review;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// Del on a step's row (Spec v0.6.8 §5 S3), pressed through WPF's own keyboard pipeline on the real
/// Review screen: the step's screenshot is taken out as Del in the strip takes it out, the same words are
/// announced, the undo is on the note where the technician is looking, and Ctrl+Z takes it back and puts
/// focus on the screenshot again. And the two buttons under an enlarged screenshot, which are one height.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class StepFrameDeleteTests(TheApplication app)
{
    [Fact]
    public async Task DelOnAStepsFrameDeletesItAndCtrlZBringsItBackWithFocus()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review");
            try
            {
                var review = Screens.Review(shell);
                var note = Audit.Descendants(shell).OfType<NoteEditorView>().Single();
                var frame = Audit.Descendants(note).OfType<Button>().First(b => b.DataContext is FrameChip && b.IsVisible);
                var chip = (FrameChip)frame.DataContext;
                var before = review.Strip.IncludedFrameIds;
                Assert.Contains(chip.Id, before);
                Assert.Contains("Del deletes it", AutomationProperties.GetHelpText(frame), StringComparison.Ordinal);

                _ = frame.Focus();
                await Audit.Settle();
                Assert.True(frame.IsKeyboardFocused, "The first step's frame did not take keyboard focus.");

                var heard = await LiveRegionTests.HeardAsync(() => Assert.True(Keys.Press(shell, Key.Delete), "Del on a step's frame was not handled."));

                Assert.False(chip.Present, "Del left the screenshot on its step.");
                Assert.DoesNotContain(chip.Id, review.Strip.IncludedFrameIds);
                Assert.True(review.Note.HasUndoToast);
                Assert.Contains($"Deleted {chip.Label}", heard);

                // The undo is on the note, named, with its key; the strip was never opened.
                Assert.False(review.Strip.Shown, "Del on a step's frame opened the screenshots.");
                var undo = Audit.Descendants(note).OfType<Button>().Single(b => b.IsVisible && AutomationProperties.GetName(b) == "Undo");
                Assert.Equal("Ctrl+Z", AutomationProperties.GetAcceleratorKey(undo));
                Assert.True(undo.ActualHeight >= Audit.HitTarget && undo.ActualWidth >= Audit.HitTarget, "The note's Undo is smaller than the hit target.");

                // Focus is not left on a button that is gone: it is in the step's own text.
                await Screens.UntilAsync(() => Keyboard.FocusedElement is TextBox);
                Assert.True(Keyboard.FocusedElement is TextBox box && Screens.Within(box, note), $"After Del focus is on {Keyboard.FocusedElement?.GetType().Name ?? "nothing"}, not in the step's text.");

                // Ctrl+Z there, with nothing typed since, is the delete's undo, not the text box's.
                Assert.True(Keys.Press(shell, Key.Z, ModifierKeys.Control), "Ctrl+Z after Del was not handled.");
                await Screens.UntilAsync(() => chip.Present && Keyboard.FocusedElement is Button);

                Assert.True(chip.Present, "Ctrl+Z did not bring the screenshot back to its step.");
                Assert.Equal(before, review.Strip.IncludedFrameIds);
                Assert.False(review.Note.HasUndoToast);
                Assert.True(Keyboard.FocusedElement is Button back && ReferenceEquals(back.DataContext, chip), $"After the undo focus is on {Keyboard.FocusedElement?.GetType().Name ?? "nothing"}, not on the step's screenshot.");
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task SpaceOnAStepsFrameStillIncludesAndExcludesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review");
            try
            {
                var review = Screens.Review(shell);
                var note = Audit.Descendants(shell).OfType<NoteEditorView>().Single();
                var frame = Audit.Descendants(note).OfType<Button>().First(b => b.DataContext is FrameChip && b.IsVisible);
                var chip = (FrameChip)frame.DataContext;
                _ = frame.Focus();
                await Audit.Settle();

                Assert.True(Keys.Press(shell, Key.Space), "Space on a step's frame was not handled.");
                await Screens.UntilAsync(() => chip.Place == "not attached");
                Assert.Equal("not attached", chip.Place);
                Assert.True(chip.Present);
                Assert.False(review.Note.HasUndoToast);
                Assert.False(review.Strip.IsEnlarged, "Space on a step's frame enlarged it.");

                Assert.True(Keys.Press(shell, Key.Space));
                await Screens.UntilAsync(() => chip.Place != "not attached");
                Assert.StartsWith("screenshot ", chip.Place, StringComparison.Ordinal);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task TheTwoButtonsUnderAnEnlargedScreenshotAreTheSameHeight()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review-enlarged");
            try
            {
                var strip = Audit.Descendants(shell).OfType<FilmstripView>().Single();
                var ocr = Audit.Descendants(strip).OfType<Button>().Single(b => b.Content is "OCR text  T");
                var blur = Audit.Descendants(strip).OfType<ToggleButton>().Single(b => b.Name == "BlurMode");
                var control = (double)shell.FindResource("Layout.control-height");

                Assert.True(ocr.IsVisible && blur.IsVisible, "The enlarged screenshot's buttons are not shown.");
                Assert.Equal(control, ocr.ActualHeight);
                Assert.Equal(control, blur.ActualHeight);

                // Side by side on one line, top to top.
                var top = ocr.TranslatePoint(default, strip).Y;
                Assert.Equal(top, blur.TranslatePoint(default, strip).Y);

                // Armed, the hint beside them wraps the row; the two are still one height.
                _ = Keys.Press(shell, Key.B);
                await Screens.UntilAsync(() => blur.IsChecked == true);
                await Audit.Settle();
                Assert.Equal(control, ocr.ActualHeight);
                Assert.Equal(control, blur.ActualHeight);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }
}
