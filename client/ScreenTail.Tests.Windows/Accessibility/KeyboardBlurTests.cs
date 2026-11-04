using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Review;
using ScreenTail.UI.Shell;
using Rect = System.Windows.Rect;
using Size = ScreenTail.Core.Review.Size;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// Blurring a region from the keyboard (ST-084 follow-up; the audit's owner decision had left it to the
/// mouse). Driven through WPF's own keyboard pipeline on the shell's Review with the sample session
/// (<see cref="Keys"/>): B brings out the box with focus on it, the arrows move it, Shift with them resizes
/// it, Ctrl takes bigger steps, Enter blurs what it covers through the view model's blur, the one a mouse
/// drag calls, and Esc puts it away with nothing blurred. What the box says as it moves is heard where
/// <see cref="UI.Controls.LiveRegion"/> hands it to UI Automation, and is numbers only (INV-10).
///
/// The suite's per-screen rules (names, hit targets, focus visuals in three palettes, reduce motion, high
/// contrast, Accessibility Insights) run over the box too, as the screen "review-blur-box".
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed partial class KeyboardBlurTests(TheApplication app)
{
    [Fact]
    public async Task TheKeysPlaceTheBoxAndEnterBlursWhatItCoversAsADragWould()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review-enlarged");
            try
            {
                var strip = Screens.Review(shell).Strip;
                var frame = strip.Enlarged!;
                var before = frame.Cell.Frame.MaskedRegions.Count;
                Assert.True(frame.HasOcrText, "The sample's first screenshot has no text, so a blur taking it away proves nothing.");

                BlurBoxControl? box = null;
                var opened = await LiveRegionTests.HeardAsync(() => box = OpenBox(shell));
                await Screens.UntilAsync(() => box!.IsKeyboardFocused);
                Assert.True(box!.IsKeyboardFocused, "B did not put focus on the blur box.");
                Assert.Contains(BlurBox.Centred.Spoken, opened);

                // What a screen reader finds on the box: its name, a role that is not "custom", and the keys.
                var peer = UIElementAutomationPeer.CreatePeerForElement(box);
                Assert.Equal("Region to blur", peer.GetName());
                Assert.Equal(AutomationControlType.Custom, peer.GetAutomationControlType());
                Assert.Equal("blur box", peer.GetLocalizedControlType());
                Assert.True(peer.IsKeyboardFocusable());
                Assert.Contains("arrow keys", peer.GetHelpText(), StringComparison.Ordinal);
                Assert.Contains("Enter", peer.GetHelpText(), StringComparison.Ordinal);

                // Right three times, Shift+Right twice, Ctrl+Down, Ctrl+Shift+Down, Shift+Up.
                var heard = await LiveRegionTests.HeardAsync(() =>
                {
                    foreach (var (key, modifiers) in new (Key, ModifierKeys)[]
                    {
                        (Key.Right, ModifierKeys.None),
                        (Key.Right, ModifierKeys.None),
                        (Key.Right, ModifierKeys.None),
                        (Key.Right, ModifierKeys.Shift),
                        (Key.Right, ModifierKeys.Shift),
                        (Key.Down, ModifierKeys.Control),
                        (Key.Down, ModifierKeys.Control | ModifierKeys.Shift),
                        (Key.Up, ModifierKeys.Shift),
                    })
                    {
                        Assert.True(Keys.Press(shell, key, modifiers), $"{modifiers}+{key} on the blur box was not handled.");
                    }
                });

                var expected = BlurBox.Centred.Move(3, 10).Resize(2, 9);
                Assert.Equal("Blur box: 43% from the left, 50% from the top, 22% wide, 29% tall", expected.Spoken);
                Assert.Equal(expected.Spoken, Status(shell).Text);
                Assert.Contains(BlurBox.Centred.Move(1, 0).Spoken, heard);
                Assert.Contains(expected.Spoken, heard);
                Assert.All(heard, said => Assert.Matches(NumbersOnly(), said));
                Assert.True(box.IsKeyboardFocused, "The arrows moved focus off the blur box.");

                // Drawn where it will blur: over the picture, letterbox and all, at the rectangle that goes on.
                var image = (Image)Audit.Descendants(shell).OfType<FrameworkElement>().Single(e => e.Name == "EnlargedImage");
                var displayed = new Size(image.ActualWidth, image.ActualHeight);
                var drawn = expected.Drawn(displayed);
                var onScreen = box.TransformToVisual(image).TransformBounds(new Rect(box.RenderSize));
                Assert.True(
                    Near(onScreen.Left, drawn.Left) && Near(onScreen.Top, drawn.Top) && Near(onScreen.Width, drawn.Width) && Near(onScreen.Height, drawn.Height),
                    $"The box is drawn at {onScreen}, not over the rectangle it blurs ({drawn.Left}, {drawn.Top}, {drawn.Width}, {drawn.Height}).");

                // Enter: the view model's blur, the call a drag over the same rectangle makes, so the region
                // is BlurRegion.From's, scaled to the frame and rounded outward.
                var region = BlurRegion.From(drawn, displayed, new Size(frame.Cell.Frame.Width, frame.Cell.Frame.Height));
                Assert.True(Keys.Press(shell, Key.Enter), "Enter on the blur box was not handled.");
                await Screens.UntilAsync(() => frame.Cell.Frame.MaskedRegions.Count > before && !box.IsVisible);

                Assert.Equal(before + 1, frame.Cell.Frame.MaskedRegions.Count);
                Assert.Equal(region, frame.Cell.Frame.MaskedRegions[^1]);
                Assert.Equal(MaskKind.UserBlur, frame.Cell.Frame.MaskedRegions[^1].Kind);

                // The text read off what was just destroyed goes with it (S-18).
                Assert.Null(frame.Cell.Frame.OcrText);
                Assert.False(frame.HasOcrText);

                // Put away, the screenshot still open, focus on the toggle that brings it back, and said.
                Assert.False(box.IsVisible, "The blur box stayed out after Enter.");
                Assert.True(strip.IsEnlarged, "Enter on the blur box closed the enlarged screenshot.");
                Assert.True(ReferenceEquals(Keyboard.FocusedElement, Toggle(shell)), $"After Enter focus is on {Keyboard.FocusedElement?.GetType().Name ?? "nothing"}, not on the blur toggle.");
                Assert.False(Toggle(shell).IsChecked == true);
                Assert.Equal("Region blurred", Status(shell).Text);
                Assert.True(Status(shell).IsVisible);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task EscPutsTheBoxAwayAndBlursNothingThenASecondEscClosesTheScreenshot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review-enlarged");
            try
            {
                var strip = Screens.Review(shell).Strip;
                var frame = strip.Enlarged!;
                var regions = frame.Cell.Frame.MaskedRegions;
                var text = frame.Cell.Frame.OcrText;

                var box = await Screens.OpenBlurBoxAsync(shell);
                _ = Keys.Press(shell, Key.Right);
                _ = Keys.Press(shell, Key.Down, ModifierKeys.Shift);
                await Audit.Settle();
                Assert.True(Keys.Press(shell, Key.Escape), "Esc on the blur box was not handled.");
                await Audit.Settle();

                Assert.False(box.IsVisible, "Esc left the blur box out.");
                Assert.False(Toggle(shell).IsChecked == true, "Esc left blurring armed.");
                Assert.True(strip.IsEnlarged, "Esc on the blur box closed the screenshot instead of putting the box away.");
                Assert.True(ReferenceEquals(Keyboard.FocusedElement, Toggle(shell)), $"After Esc focus is on {Keyboard.FocusedElement?.GetType().Name ?? "nothing"}, not on the blur toggle.");
                Assert.False(Status(shell).IsVisible, "What the box said stayed on screen after it was put away.");

                // Esc puts it away from the enlarged view's buttons too, where Tab takes a keyboard user.
                box = await Screens.OpenBlurBoxAsync(shell);
                _ = Keys.Press(shell, Key.Tab);
                await Audit.Settle();
                Assert.False(box.IsKeyboardFocused, "Tab did not leave the blur box.");
                Assert.True(box.IsVisible, "Leaving the blur box with Tab put it away.");
                _ = Keys.Press(shell, Key.Escape);
                await Audit.Settle();
                Assert.False(box.IsVisible, "Esc on a button of the enlarged view left the blur box out.");
                Assert.True(strip.IsEnlarged);

                // Nothing was blurred and nothing was taken.
                Assert.Same(regions, frame.Cell.Frame.MaskedRegions);
                Assert.Equal(text, frame.Cell.Frame.OcrText);

                _ = Keys.Press(shell, Key.Escape);
                await Audit.Settle();
                Assert.False(strip.IsEnlarged, "A second Esc did not close the enlarged screenshot.");
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Fact]
    public async Task TheBoxStopsAtThePicturesEdgesAndCanCoverAllOfIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review-enlarged");
            try
            {
                var frame = Screens.Review(shell).Strip.Enlarged!;
                var before = frame.Cell.Frame.MaskedRegions.Count;
                var box = await Screens.OpenBlurBoxAsync(shell);
                var image = (Image)Audit.Descendants(shell).OfType<FrameworkElement>().Single(e => e.Name == "EnlargedImage");
                for (var i = 0; i < 12; i++)
                {
                    _ = Keys.Press(shell, Key.Right, ModifierKeys.Control);
                    _ = Keys.Press(shell, Key.Up, ModifierKeys.Control);
                }

                await Audit.Settle();
                Assert.Equal("Blur box: 80% from the left, 0% from the top, 20% wide, 20% tall", Status(shell).Text);
                var bounds = box.TransformToVisual(image).TransformBounds(new Rect(box.RenderSize));
                Assert.True(Near(bounds.Right, image.ActualWidth) && Near(bounds.Top, 0), $"The box at the edge is drawn at {bounds} on a picture {image.ActualWidth} wide.");

                // Grown past the right edge, it grows to the left; past the bottom, upward. All of it.
                for (var i = 0; i < 12; i++)
                {
                    _ = Keys.Press(shell, Key.Right, ModifierKeys.Control | ModifierKeys.Shift);
                    _ = Keys.Press(shell, Key.Down, ModifierKeys.Control | ModifierKeys.Shift);
                }

                await Audit.Settle();
                Assert.Equal("Blur box: 0% from the left, 0% from the top, 100% wide, 100% tall", Status(shell).Text);

                _ = Keys.Press(shell, Key.Enter);
                await Screens.UntilAsync(() => frame.Cell.Frame.MaskedRegions.Count > before && !box.IsVisible);
                Assert.Equal(before + 1, frame.Cell.Frame.MaskedRegions.Count);
                var region = frame.Cell.Frame.MaskedRegions[^1];
                Assert.Equal((0L, 0L, (long)frame.Cell.Frame.Width, (long)frame.Cell.Frame.Height), (region.X, region.Y, region.Width, region.Height));
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    [Theory]
    [InlineData(Key.Right, ModifierKeys.None, BlurBoxKey.Move, BlurBoxDirection.Right, false, false)]
    [InlineData(Key.Left, ModifierKeys.Shift, BlurBoxKey.Move, BlurBoxDirection.Left, true, false)]
    [InlineData(Key.Up, ModifierKeys.Control, BlurBoxKey.Move, BlurBoxDirection.Up, false, true)]
    [InlineData(Key.Down, ModifierKeys.Control | ModifierKeys.Shift, BlurBoxKey.Move, BlurBoxDirection.Down, true, true)]
    [InlineData(Key.Enter, ModifierKeys.None, BlurBoxKey.Apply, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.Escape, ModifierKeys.None, BlurBoxKey.Cancel, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.Enter, ModifierKeys.Control, BlurBoxKey.None, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.Left, ModifierKeys.Alt, BlurBoxKey.None, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.B, ModifierKeys.None, BlurBoxKey.None, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.Tab, ModifierKeys.None, BlurBoxKey.None, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.Z, ModifierKeys.Control, BlurBoxKey.None, BlurBoxDirection.Left, false, false)]
    public void EachKeyDoesWhatTheSheetSays(Key key, ModifierKeys modifiers, BlurBoxKey kind, BlurBoxDirection direction, bool resize, bool large)
    {
        // Ctrl+Enter stays Publish (Spec §5 S3), B stays the toggle that puts the box away, Tab leaves it,
        // Ctrl+Z still undoes a delete, and Alt is nobody's here.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        Assert.Equal(new BlurBoxPress(kind, direction, resize, large), BlurBoxKeys.Route(key, modifiers));
    }

    /// <summary>The shortcut sheet (Spec §4) lists the box's keys where the screenshots' keys are.</summary>
    [Fact]
    public void TheShortcutSheetListsTheBlurBoxKeys()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var screenshots = ShortcutSheet.Groups.Single(g => g.Name == "In the screenshots").Chords;
        foreach (var keys in new[] { "B", "Arrows", "Shift+arrows", "Ctrl+arrows", "Enter, Esc" })
        {
            Assert.Contains(screenshots, chord => chord.Keys == keys);
        }

        Assert.Contains("blur box", screenshots.Single(c => c.Keys == "Arrows").Action, StringComparison.Ordinal);
        Assert.Contains("box", screenshots.Single(c => c.Keys == "Enter, Esc").Action, StringComparison.Ordinal);
    }

    /// <summary>Fixed words around whole numbers, or that the blur landed. Never a word read off the picture.</summary>
    [GeneratedRegex(@"^(Blur box: \d{1,3}% from the left, \d{1,3}% from the top, \d{1,3}% wide, \d{1,3}% tall|Region blurred)$")]
    private static partial Regex NumbersOnly();

    private static BlurBoxControl OpenBox(Window shell)
    {
        Assert.True(Keys.Press(shell, Key.B), "B on the enlarged screenshot was not handled.");
        return Audit.Descendants(shell).OfType<BlurBoxControl>().Single();
    }

    private static ToggleButton Toggle(Window shell) =>
        Audit.Descendants(shell).OfType<ToggleButton>().Single(b => b.Name == "BlurMode");

    private static TextBlock Status(Window shell) =>
        Audit.Descendants(shell).OfType<TextBlock>().Single(b => b.Name == "BlurBoxStatus");

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.5;
}
