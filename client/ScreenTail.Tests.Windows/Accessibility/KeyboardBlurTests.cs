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
using Frame = ScreenTail.Shared.Schema.Frame;
using MouseButton = System.Windows.Input.MouseButton;
using Point = System.Windows.Point;
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
                var said = await LiveRegionTests.HeardAsync(async () =>
                {
                    Assert.True(Keys.Press(shell, Key.Enter), "Enter on the blur box was not handled.");
                    await Screens.UntilAsync(() => frame.Cell.Frame.MaskedRegions.Count > before && !box.IsVisible);
                });

                Assert.Contains("Region blurred", said);
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
                var heard = await LiveRegionTests.HeardAsync(() =>
                {
                    for (var i = 0; i < 12; i++)
                    {
                        _ = Keys.Press(shell, Key.Right, ModifierKeys.Control);
                        _ = Keys.Press(shell, Key.Up, ModifierKeys.Control);
                    }
                });

                // The presses that changed nothing say so, each one, rather than going silent.
                Assert.Equal("Blur box: 80% from the left, 0% from the top, 20% wide, 20% tall, as far as it goes", Status(shell).Text);
                Assert.True(heard.Count(said => said.EndsWith(", as far as it goes", StringComparison.Ordinal)) >= 8, "An arrow into the edge was not announced: " + string.Join(" | ", heard));
                Assert.All(heard, said => Assert.Matches(NumbersOnly(), said));
                var bounds = box.TransformToVisual(image).TransformBounds(new Rect(box.RenderSize));
                Assert.True(Near(bounds.Right, image.ActualWidth) && Near(bounds.Top, 0), $"The box at the edge is drawn at {bounds} on a picture {image.ActualWidth} wide.");

                // Grown past the right edge, it grows to the left; past the bottom, upward. All of it.
                for (var i = 0; i < 12; i++)
                {
                    _ = Keys.Press(shell, Key.Right, ModifierKeys.Control | ModifierKeys.Shift);
                    _ = Keys.Press(shell, Key.Down, ModifierKeys.Control | ModifierKeys.Shift);
                }

                await Audit.Settle();
                Assert.Equal("Blur box: 0% from the left, 0% from the top, 100% wide, 100% tall, as far as it goes", Status(shell).Text);

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

    /// <summary>
    /// Enter held down (ST-084 follow-up review). The first press blurs and puts the box away with focus on
    /// the toggle; a repeat that reached the toggle would bring the box back, and the next would blur a
    /// second region nobody placed. Exactly one region is written.
    /// </summary>
    [Fact]
    public async Task HoldingEnterBlursOnceAndNeverASecondRegion()
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

                Assert.True(Keys.Down(shell, Key.Enter), "Enter on the blur box was not handled.");
                await Screens.UntilAsync(() => frame.Cell.Frame.MaskedRegions.Count > before && !box.IsVisible);
                Assert.Equal(before + 1, frame.Cell.Frame.MaskedRegions.Count);
                for (var i = 0; i < 10; i++)
                {
                    _ = Keys.Down(shell, Key.Enter, repeat: true);
                    await Audit.Settle();
                    await Task.Delay(20);
                    await Audit.Settle();
                }

                Keys.Up(shell, Key.Enter);
                for (var i = 0; i < 20; i++)
                {
                    await Task.Delay(20);
                    await Audit.Settle();
                }

                Assert.Equal(before + 1, frame.Cell.Frame.MaskedRegions.Count);
                Assert.False(box.IsVisible, "A held Enter brought the blur box back.");
                Assert.False(Toggle(shell).IsChecked == true, "A held Enter armed blurring again.");
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    /// <summary>
    /// A blur the service did not write (ST-084 follow-up review): said, in fixed words, and the box stays
    /// out where it was with focus on it, so Enter tries again. The second time it lands, at that place.
    /// </summary>
    [Fact]
    public async Task ABlurThatDidNotLandIsSaidAndTheBoxStaysWhereItWasForAnotherTry()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var frames = new AnsweringFrames(Picture(1600, 900)) { Reply = BlurReply.NotWritten };
            var (window, strip) = await HostAsync(frames);
            try
            {
                var frame = strip.Enlarged!;
                var text = frame.Cell.Frame.OcrText;
                var box = await Screens.OpenBlurBoxAsync(window);
                _ = Keys.Press(window, Key.Right);
                _ = Keys.Press(window, Key.Right);
                var placed = BlurBox.Centred.Move(2, 0);

                var said = await LiveRegionTests.HeardAsync(async () =>
                {
                    Assert.True(Keys.Press(window, Key.Enter), "Enter on the blur box was not handled.");
                    await Screens.UntilAsync(() => frames.Asked == 1 && Status(window).Text.StartsWith("Nothing", StringComparison.Ordinal));
                });

                Assert.Equal(1, frames.Asked);
                Assert.Contains(NotBlurred, said);
                Assert.Equal(NotBlurred, Status(window).Text);
                Assert.True(box.IsVisible, "A blur that did not land threw the box away.");
                Assert.True(box.IsKeyboardFocused, "A blur that did not land took focus off the box.");
                Assert.True(Toggle(window).IsChecked == true, "A blur that did not land disarmed blurring.");
                Assert.Empty(frame.Cell.Frame.MaskedRegions);
                Assert.Equal(text, frame.Cell.Frame.OcrText);

                // Again, and this time the service writes it: the region is the one the box was left at.
                frames.Reply = null;
                var image = Image(window);
                var displayed = new Size(image.ActualWidth, image.ActualHeight);
                var expected = BlurRegion.From(placed.Drawn(displayed), displayed, new Size(frame.Cell.Frame.Width, frame.Cell.Frame.Height));
                _ = Keys.Press(window, Key.Enter);
                await Screens.UntilAsync(() => frame.Cell.Frame.MaskedRegions.Count == 1 && !box.IsVisible);
                Assert.Equal(2, frames.Asked);
                Assert.Equal(expected, Assert.Single(frame.Cell.Frame.MaskedRegions));
                Assert.Equal(expected, frames.Regions[^1]);
                Assert.Equal("Region blurred", Status(window).Text);
            }
            finally
            {
                await Screens.CloseAsync(window);
            }
        });
    }

    /// <summary>
    /// Written, but the new image too large to send back (ST-084 follow-up review): the pane stops showing
    /// the picture and text from before, the strip shows the service's thumbnail of what is on disk, and
    /// that is said.
    /// </summary>
    [Fact]
    public async Task ABlurWrittenButNotSentBackStopsShowingThePictureFromBefore()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var frames = new AnsweringFrames(Picture(1600, 900)) { Reply = new BlurReply(true, null), TooLargeOnceBlurred = true };
            var (window, strip) = await HostAsync(frames);
            try
            {
                var frame = strip.Enlarged!;
                var box = await Screens.OpenBlurBoxAsync(window);
                var thumbnails = frames.Thumbnails;
                _ = Keys.Press(window, Key.Enter);
                await Screens.UntilAsync(() => frame.Cell.Frame.MaskedRegions.Count == 1 && frames.Thumbnails > thumbnails);

                Assert.Single(frame.Cell.Frame.MaskedRegions);
                Assert.Null(frame.Cell.Frame.OcrText);
                Assert.Null(frame.OcrText);
                Assert.Null(frame.Image);
                Assert.True(frames.Thumbnails > thumbnails, "The strip did not ask for the picture as it now is.");
                Assert.NotNull(frame.Thumbnail);
                Assert.False(box.IsVisible);
                Assert.StartsWith("Region blurred. The screenshot is now too large to show here", Status(window).Text, StringComparison.Ordinal);
            }
            finally
            {
                await Screens.CloseAsync(window);
            }
        });
    }

    /// <summary>
    /// The pipe has no timeout of its own (ST-084 follow-up review). The pane stops waiting after
    /// <see cref="FilmstripView.BlurWait"/> and says so, but the blur goes on and the frame stays pending
    /// (review of #257): its picture, thumbnail and text are no longer shown, opening it again shows a
    /// placeholder and asks the service for nothing, Publish is held back, B then Enter says it is still
    /// waiting, and when the answer comes it is said and the frame is shown as the service has it.
    /// </summary>
    [Fact]
    public async Task ABlurTheServiceIsSlowToAnswerKeepsTheFrameHiddenAndPublishHeldUntilItDoes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var wait = FilmstripView.BlurWait;
        try
        {
            await app.RunAsync(async () =>
            {
                FilmstripView.BlurWait = TimeSpan.FromMilliseconds(300);
                var frames = new AnsweringFrames(Picture(1600, 900)) { Gate = new TaskCompletionSource<BlurReply>() };
                var (window, strip) = await HostAsync(frames);
                try
                {
                    var frame = strip.Enlarged!;
                    var box = await Screens.OpenBlurBoxAsync(window);
                    _ = Keys.Press(window, Key.Enter);
                    await Screens.UntilAsync(() => Status(window).Text.StartsWith("The capture service has not answered yet", StringComparison.Ordinal));

                    Assert.Equal("The capture service has not answered yet. The screenshot is hidden and Publish waits until it does.", Status(window).Text);
                    Assert.False(box.IsVisible);
                    Assert.True(frame.BlurPending);
                    Assert.Null(frame.Image);
                    Assert.Null(frame.Thumbnail);
                    Assert.Null(frame.OcrText);
                    Assert.True(frame.WaitingForBlur, "The enlarged view shows nothing to say it is waiting.");
                    Assert.Equal("A blur of frame 1 is still being applied. Publish once it has landed.", strip.PublishHeldBecause);
                    Assert.Empty(frame.Cell.Frame.MaskedRegions);

                    // Closed and opened again while it waits: nothing is asked for, so nothing old is shown.
                    var images = frames.Images;
                    _ = Keys.Press(window, Key.Escape);
                    await Audit.Settle();
                    Assert.False(strip.IsEnlarged);
                    _ = await Screens.FocusFirstFrameAsync(window);
                    _ = Keys.Press(window, Key.Enter);
                    await Screens.UntilAsync(() => strip.IsEnlarged);
                    await Audit.Settle();
                    Assert.True(strip.IsEnlarged);
                    Assert.Null(frame.Image);
                    Assert.Equal(images, frames.Images);
                    Assert.True(frame.WaitingForBlur);

                    // B then Enter on the same frame: said, not ignored, and not asked of the service again.
                    box = await Screens.OpenBlurBoxAsync(window);
                    var said = await LiveRegionTests.HeardAsync(() => Assert.True(Keys.Press(window, Key.Enter)));
                    Assert.Contains("Still waiting for the last blur.", said);
                    Assert.Equal(1, frames.Asked);
                    _ = Keys.Press(window, Key.Escape);
                    await Audit.Settle();

                    // The answer: said, the hold lifted, and the frame fetched again as the service has it.
                    var answered = await LiveRegionTests.HeardAsync(async () =>
                    {
                        frames.Gate.SetResult(BlurReply.NotWritten);
                        await Screens.UntilAsync(() => !frame.BlurPending && frame.Image is not null && frame.Thumbnail is not null);
                    });

                    Assert.Contains("The last blur was not applied.", answered);
                    Assert.Null(strip.PublishHeldBecause);
                    Assert.NotNull(frame.Image);
                    Assert.NotNull(frame.Thumbnail);
                    Assert.False(frame.WaitingForBlur);
                    Assert.Equal("Password: hunter2", frame.OcrText);
                }
                finally
                {
                    await Screens.CloseAsync(window);
                }
            });
        }
        finally
        {
            FilmstripView.BlurWait = wait;
        }
    }

    /// <summary>
    /// Esc while a blur waits puts the box away at once (ST-084 follow-up review); B and Enter then say it is
    /// still waiting rather than nothing (review of #257), and the answer, when it comes, is said.
    /// </summary>
    [Fact]
    public async Task EscWhileABlurWaitsThenBAndEnterSayItIsStillWaiting()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var frames = new AnsweringFrames(Picture(1600, 900)) { Gate = new TaskCompletionSource<BlurReply>() };
            var (window, strip) = await HostAsync(frames);
            try
            {
                var box = await Screens.OpenBlurBoxAsync(window);
                _ = Keys.Press(window, Key.Enter);
                await Screens.UntilAsync(() => frames.Asked == 1);
                Assert.True(Keys.Press(window, Key.Escape), "Esc while the blur waited was not handled.");
                await Audit.Settle();
                Assert.False(box.IsVisible, "Esc while the blur waited left the box out.");
                Assert.True(strip.IsEnlarged, "Esc while the blur waited closed the screenshot.");

                box = await Screens.OpenBlurBoxAsync(window);
                var said = await LiveRegionTests.HeardAsync(() => Assert.True(Keys.Press(window, Key.Enter)));
                Assert.Contains("Still waiting for the last blur.", said);
                Assert.Equal(1, frames.Asked);

                var answered = await LiveRegionTests.HeardAsync(async () =>
                {
                    frames.Gate.SetResult(new BlurReply(true, Picture(1600, 900)));
                    await Screens.UntilAsync(() => Status(window).Text == "Region blurred");
                });

                Assert.Contains("Region blurred", answered);
                Assert.Single(strip.Enlarged!.Cell.Frame.MaskedRegions);
                Assert.Null(strip.PublishHeldBecause);
            }
            finally
            {
                await Screens.CloseAsync(window);
            }
        });
    }

    /// <summary>
    /// The pipe dropped after the blur went out (review of #257): it may be on disk, so the pane does not say
    /// nothing was blurred. It says it may not have been applied, and shows the frame as the service has it.
    /// </summary>
    [Fact]
    public async Task ABlurWhoseAnswerWasLostSaysItMayNotHaveBeenApplied()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var frames = new AnsweringFrames(Picture(1600, 900)) { Reply = BlurReply.Unknown };
            var (window, strip) = await HostAsync(frames);
            try
            {
                var frame = strip.Enlarged!;
                var images = frames.Images;
                var box = await Screens.OpenBlurBoxAsync(window);
                _ = Keys.Press(window, Key.Enter);
                await Screens.UntilAsync(() => Status(window).Text.StartsWith("The blur may not have been applied", StringComparison.Ordinal) && !frame.BlurPending && frame.Image is not null);

                Assert.Equal("The blur may not have been applied: the capture service went away. The screenshot is shown again as the service has it.", Status(window).Text);
                Assert.DoesNotContain("Nothing was blurred", Status(window).Text, StringComparison.Ordinal);
                Assert.False(box.IsVisible);
                Assert.True(frames.Images > images, "The picture was not asked for again.");
                Assert.NotNull(frame.Thumbnail);
                Assert.Null(strip.PublishHeldBecause);
            }
            finally
            {
                await Screens.CloseAsync(window);
            }
        });
    }

    /// <summary>
    /// A click on the armed picture, a drag of nothing (review of #257): no attempt. Nothing is asked of the
    /// service, nothing is said, no keyboard box comes out for a mouse user, and blurring stays armed. At
    /// chosen points, in the picture's coordinates, through the drag's own methods: in the middle, on the
    /// corners and an edge, outside the picture on either side, and a wobble of three pixels. Then a real
    /// drag that starts outside the picture, which is blurred, clamped to the picture.
    ///
    /// Not raised as mouse events at the real pointer, as it first was: WPF reads an event's position from
    /// the last pointer report, and capturing the mouse on a window the pointer was never reported over
    /// reports wherever the real pointer sits. A raised press and release then read two different points,
    /// a drag from nowhere to the laptop's pointer, and this failed on the laptop whenever its pointer was
    /// somewhere the hosted runner's is not (PR #262, hardware-checks run 42038458543).
    /// </summary>
    [Fact]
    public async Task AClickOnTheArmedPictureIsNoAttempt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var frames = new AnsweringFrames(Picture(1600, 900));
            var (window, strip) = await HostAsync(frames);
            try
            {
                var view = Audit.Descendants(window).OfType<FilmstripView>().Single();
                var image = Image(window);
                var box = await Screens.OpenBlurBoxAsync(window);

                // Measured once the box is out: arming shows the hint and the status line under the picture,
                // which draws it smaller than before.
                var (w, h) = (image.ActualWidth, image.ActualHeight);
                var clicks = new (string Where, Point From, Point To)[]
                {
                    ("in the middle", new(w / 2, h / 2), new(w / 2, h / 2)),
                    ("on the top left corner", new(0, 0), new(0, 0)),
                    ("on the bottom right corner", new(w, h), new(w, h)),
                    ("on the right edge", new(w, h / 2), new(w, h / 2)),
                    ("outside, above and to the left", new(-40, -40), new(-40, -40)),
                    ("outside, to the right", new(w + 60, h / 2), new(w + 60, h / 2)),
                    ("that wobbled three pixels", new(w / 3, h / 3), new((w / 3) + 3, (h / 3) + 3)),
                };

                foreach (var (where, from, to) in clicks)
                {
                    var said = await LiveRegionTests.HeardAsync(async () =>
                    {
                        view.BeginDrag(from);
                        await view.EndDragAsync(to);
                    });

                    Assert.True(frames.Asked == 0, $"A click {where} asked the service for a blur.");
                    Assert.DoesNotContain(said, words => words.StartsWith("Nothing was blurred", StringComparison.Ordinal));
                    Assert.False(box.IsVisible, $"A click {where} brought the keyboard box back out.");
                    Assert.True(Toggle(window).IsChecked == true, $"A click {where} disarmed blurring.");
                    Assert.Empty(strip.Enlarged!.Cell.Frame.MaskedRegions);
                }

                // The control: a real drag, from outside the picture into it, is a blur, clamped to the picture.
                var frame = strip.Enlarged!;
                var dragged = Core.Review.Rect.Between(-40, -40, w * 0.3, h * 0.3);
                var expected = BlurRegion.From(dragged, new Size(w, h), new Size(frame.Cell.Frame.Width, frame.Cell.Frame.Height));
                view.BeginDrag(new Point(-40, -40));
                await view.EndDragAsync(new Point(w * 0.3, h * 0.3));
                await Screens.UntilAsync(() => frame.Cell.Frame.MaskedRegions.Count == 1);
                Assert.Equal(1, frames.Asked);
                Assert.Equal(expected, Assert.Single(frame.Cell.Frame.MaskedRegions));
                Assert.Equal((0L, 0L), (expected.X, expected.Y));
            }
            finally
            {
                await Screens.CloseAsync(window);
            }
        });
    }

    /// <summary>
    /// While a blur waits for the service, Publish is unavailable every way there is to press it (review of
    /// #257): the button is disabled with the reason beside it and as its help text, Ctrl+Enter does nothing,
    /// and Alt+3 then Enter or Space does nothing. Once the blur lands, Publish works again.
    /// </summary>
    [Fact]
    public async Task PublishIsHeldBackEverywhereWhileABlurWaits()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var session = SessionFixture.Load();
            var frames = new AnsweringFrames(Picture(1600, 900)) { Gate = new TaskCompletionSource<BlurReply>() };
            var ticket = new Core.Review.Publish.TicketMatch("48213", "Printer offline", "Acme Dental");
            var published = 0;
            var panel = new Core.Review.Publish.PublishPanel(
                session,
                ["connectwise"],
                (_, _) => Task.FromResult<IReadOnlyList<Core.Review.Publish.TicketMatch>>([ticket]),
                (request, _) =>
                {
                    published++;
                    return Task.FromResult<IReadOnlyList<Core.Review.Publish.DestinationResult>>([.. request.Destinations.Select(d => new Core.Review.Publish.DestinationResult(d, true))]);
                });
            panel.Suggest(ticket);
            var review = new ReviewViewModel(session, frames, publish: panel);
            var window = new Window
            {
                Content = new ReviewPane { DataContext = review },
                Style = (Style)Application.Current.FindResource("Window.Base"),
            };
            _ = await Screens.ShowAsync(window, 1024, 680);
            try
            {
                Assert.True(review.Publish.CanPublish, "The sample cannot publish, so this would prove nothing.");
                await EnlargeFirstAsync(window, review.Strip);
                _ = await Screens.OpenBlurBoxAsync(window);
                _ = Keys.Press(window, Key.Enter);
                await Screens.UntilAsync(() => frames.Asked == 1 && !review.Publish.CanPublish);

                var reason = review.Strip.PublishHeldBecause;
                Assert.False(string.IsNullOrEmpty(reason));
                Assert.False(review.Publish.CanPublish);
                Assert.Equal(reason, review.Publish.BlockedBecause);
                Assert.Equal(Visibility.Visible, review.Publish.BlockedVisibility);
                var button = Audit.Descendants(window).OfType<Button>().Single(b => b.Command == review.Publish.PublishCommand && b.IsVisible);
                Assert.False(button.IsEnabled, "Publish stayed enabled while a blur waited.");
                Assert.Equal(reason, System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(button).GetHelpText());

                // Every way to press it: Ctrl+Enter (the box put away first), Alt+3 into the pane and Tab
                // through it, where a disabled button is never reached to be pressed with Enter or Space, and
                // the command itself, which a stale binding could still call.
                _ = Keys.Press(window, Key.Escape);
                await Audit.Settle();
                _ = Keys.Press(window, Key.Enter, ModifierKeys.Control);
                // Alt+3 is the shell's route into the pane (KeyboardShortcutTests holds it there); this window
                // is the pane on its own, so focus is put into it the way Alt+3 does if the chord did not.
                var pane = Audit.Descendants(window).OfType<UI.Review.Publish.PublishPane>().Single();
                _ = Keys.Press(window, Key.D3, ModifierKeys.Alt);
                await Audit.Settle();
                if (!Screens.Within(Keyboard.FocusedElement, pane))
                {
                    _ = pane.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                    await Audit.Settle();
                }

                Assert.True(Screens.Within(Keyboard.FocusedElement, pane), $"Focus could not be put in the publish pane; it is on {Keyboard.FocusedElement?.GetType().Name ?? "nothing"}.");
                for (var i = 0; i < 15; i++)
                {
                    Assert.False(button.IsKeyboardFocused, "Tab reached Publish while a blur waited, where Enter or Space would press it.");
                    _ = Keys.Press(window, Key.Tab);
                    await Audit.Settle();
                }

                await review.Publish.PublishCommand.ExecuteAsync(null);
                for (var i = 0; i < 10; i++)
                {
                    await Audit.Settle();
                }

                Assert.Equal(0, published);
                Assert.NotEqual(Visibility.Visible, review.Publish.ResultsVisibility);

                // Landed: the hold lifts, and the same chord publishes.
                frames.Gate.SetResult(new BlurReply(true, Picture(1600, 900)));
                await Screens.UntilAsync(() => review.Publish.CanPublish);
                Assert.True(review.Publish.CanPublish, "Publish stayed held after the blur landed.");
                await review.Publish.PublishCommand.ExecuteAsync(null);
                await Screens.UntilAsync(() => published == 1);
                Assert.Equal(1, published);
            }
            finally
            {
                await Screens.CloseAsync(window);
            }
        });
    }

    /// <summary>
    /// The OCR text panel covers the picture (ST-084 follow-up review). T puts the box away rather than
    /// leave Enter able to blur a box nobody can see; B with the text showing closes the text first.
    /// </summary>
    [Fact]
    public async Task TheTextPanelAndTheBlurBoxAreNeverOutTogether()
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
                var box = await Screens.OpenBlurBoxAsync(shell);
                _ = Keys.Press(shell, Key.T);
                await Audit.Settle();
                Assert.True(strip.ShowOcrText, "T did not show the text.");
                Assert.False(box.IsVisible, "The blur box stayed out under the text panel.");
                Assert.False(Toggle(shell).IsChecked == true, "T left blurring armed under the text panel.");
                Assert.Same(regions, frame.Cell.Frame.MaskedRegions);

                // Focus is back on the toggle, where Enter or Space arms it again: not a blur, a box.

                box = await Screens.OpenBlurBoxAsync(shell);
                Assert.False(strip.ShowOcrText, "B left the text panel over the blur box.");
                Assert.True(box.IsVisible);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    /// <summary>
    /// Ctrl+Enter is Publish (Spec §5 S3). With a box placed and not applied it is held back and the reason
    /// said: publishing then would send the picture the technician was about to cover.
    /// </summary>
    [Fact]
    public async Task CtrlEnterDoesNotPublishWhileABoxIsOutUnapplied()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var shell = await Screens.OpenAsync("review-enlarged");
            try
            {
                var review = Screens.Review(shell);
                Assert.True(review.Publish.CanPublish, "The sample cannot publish, so this would prove nothing.");
                var box = await Screens.OpenBlurBoxAsync(shell);
                Assert.True(Keys.Press(shell, Key.Enter, ModifierKeys.Control), "Ctrl+Enter with the box out was not handled.");
                for (var i = 0; i < 10; i++)
                {
                    await Audit.Settle();
                }

                Assert.NotEqual(Visibility.Visible, review.Publish.ResultsVisibility);
                Assert.True(box.IsVisible);
                Assert.Equal("Press Enter to blur or Esc to put the box away, then publish.", Status(shell).Text);
            }
            finally
            {
                await Screens.CloseAsync(shell);
            }
        });
    }

    /// <summary>
    /// A picture that is letterboxed at the test window's size, a tall one in a wide pane: the box is drawn
    /// at the picture's offset, through the one placement the drag's rectangle uses too. The drag itself is
    /// not driven: its position comes from the pointer, which this suite does not move.
    /// </summary>
    [Fact]
    public async Task OnALetterboxedPictureTheBoxIsDrawnOverThePictureNotTheSurface()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await app.RunAsync(async () =>
        {
            var frames = new AnsweringFrames(Picture(400, 900));
            var (window, strip) = await HostAsync(frames, 400, 900);
            try
            {
                var box = await Screens.OpenBlurBoxAsync(window);
                var image = Image(window);
                var surface = Audit.Descendants(window).OfType<Canvas>().Single(c => c.Name == "BlurSurface");
                var origin = image.TranslatePoint(default, surface);
                Assert.True(origin.X > 50, $"The picture is not letterboxed (its left edge is at {origin.X}), so this checks nothing.");

                var drawn = BlurBox.Centred.Drawn(new Size(image.ActualWidth, image.ActualHeight));
                var onSurface = box.TransformToVisual(surface).TransformBounds(new Rect(box.RenderSize));
                Assert.True(
                    Near(onSurface.Left, origin.X + drawn.Left) && Near(onSurface.Top, origin.Y + drawn.Top),
                    $"The box is at {onSurface.Left}, {onSurface.Top} on the surface, not over the picture at {origin.X + drawn.Left}, {origin.Y + drawn.Top}.");
            }
            finally
            {
                await Screens.CloseAsync(window);
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
    [InlineData(Key.Enter, ModifierKeys.Control, BlurBoxKey.HoldPublish, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.Enter, ModifierKeys.Shift, BlurBoxKey.None, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.Left, ModifierKeys.Alt, BlurBoxKey.None, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.B, ModifierKeys.None, BlurBoxKey.None, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.Tab, ModifierKeys.None, BlurBoxKey.None, BlurBoxDirection.Left, false, false)]
    [InlineData(Key.Z, ModifierKeys.Control, BlurBoxKey.None, BlurBoxDirection.Left, false, false)]
    public void EachKeyDoesWhatTheSheetSays(Key key, ModifierKeys modifiers, BlurBoxKey kind, BlurBoxDirection direction, bool resize, bool large)
    {
        // Ctrl+Enter (Publish, Spec §5 S3) is held back while a box is out, B stays the toggle that puts
        // the box away, Tab leaves it, Ctrl+Z still undoes a delete, and Alt is nobody's here.
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
    [GeneratedRegex(@"^(Blur box: \d{1,3}% from the left, \d{1,3}% from the top, \d{1,3}% wide, \d{1,3}% tall(, as far as it goes)?|Region blurred)$")]
    private static partial Regex NumbersOnly();

    private const string NotBlurred = "Nothing was blurred. Press Enter to try again, or Esc to put the box away.";

    /// <summary>
    /// The screenshots pane on its own, over <paramref name="frames"/>, with its one frame enlarged: for the
    /// answers the sample's frames never give (a refusal, a blur too large to send back, no answer at all)
    /// and for a picture of a shape the sample does not have.
    /// </summary>
    private static async Task<(Window Window, FilmstripViewModel Strip)> HostAsync(AnsweringFrames frames, int width = 1600, int height = 900)
    {
        var session = Review.ReviewSessions.Session(Review.ReviewSessions.Frame("f1", 1000) with { Width = width, Height = height, OcrText = "Password: hunter2" });
        var strip = new FilmstripViewModel(session, frames);
        var window = new Window
        {
            Content = new FilmstripView { DataContext = strip },
            Style = (Style)Application.Current.FindResource("Window.Base"),
        };
        _ = await Screens.ShowAsync(window, 1024, 680);
        await EnlargeFirstAsync(window, strip);
        return (window, strip);
    }

    private static async Task EnlargeFirstAsync(Window window, FilmstripViewModel strip)
    {
        _ = await Screens.FocusFirstFrameAsync(window);
        _ = Keys.Press(window, Key.Enter);
        await Screens.UntilAsync(() => strip.IsEnlarged && strip.Enlarged!.Image is not null);
        for (var i = 0; i < 5; i++)
        {
            await Audit.Settle();
        }

        Assert.True(strip.IsEnlarged, "Enter on the screenshot did not enlarge it.");
    }

    /// <summary>A light grey PNG with a dark band, so it decodes and has something on it.</summary>
    private static byte[] Picture(int width, int height)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(243, 243, 243));
            using var band = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(30, 30, 30));
            graphics.FillRectangle(band, 0, height / 3, width, height / 6);
        }

        using var buffer = new MemoryStream();
        bitmap.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
        return buffer.ToArray();
    }

    /// <summary>
    /// A service that answers a blur as the test says: <see cref="Reply"/> when set, held on
    /// <see cref="Gate"/> until the test answers, and otherwise written with the picture handed back.
    /// Counts what it was asked and keeps every region.
    /// </summary>
    private sealed class AnsweringFrames(byte[] picture) : IReviewFrames
    {
        public BlurReply? Reply { get; set; }

        public TaskCompletionSource<BlurReply>? Gate { get; set; }

        public int Asked { get; private set; }

        public int Thumbnails { get; private set; }

        public int Images { get; private set; }

        /// <summary>
        /// The service's answer for a frame too large for the pipe once it has been blurred: no picture,
        /// though the thumbnail, which the service scales, still comes.
        /// </summary>
        public bool TooLargeOnceBlurred { get; init; }

        public List<MaskedRegion> Regions { get; } = [];

        public Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default)
        {
            Images++;
            return Task.FromResult(TooLargeOnceBlurred && Asked > 0 ? null : (byte[]?)picture);
        }

        public Task<byte[]?> ThumbnailAsync(Frame frame, CancellationToken ct = default)
        {
            Thumbnails++;
            return Task.FromResult<byte[]?>(picture);
        }

        public Task<bool> SetIncludedAsync(string frameId, bool included, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> DeleteAsync(string frameId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<byte[]?> BlurAsync(string frameId, MaskedRegion region, CancellationToken ct = default) => throw new NotSupportedException("The pane asks for the reply.");

        public async Task<BlurReply> BlurWithReplyAsync(string frameId, MaskedRegion region, CancellationToken ct = default)
        {
            Asked++;
            Regions.Add(region);
            if (Gate is { } gate)
            {
                return await gate.Task.WaitAsync(ct);
            }

            return Reply ?? new BlurReply(true, picture);
        }
    }

    private static Image Image(Window window) =>
        (Image)Audit.Descendants(window).OfType<FrameworkElement>().Single(e => e.Name == "EnlargedImage");

    private static BlurBoxControl OpenBox(Window shell)
    {
        Assert.True(Keys.Press(shell, Key.B), "B on the enlarged screenshot was not handled.");
        return Audit.Descendants(shell).OfType<BlurBoxControl>().Single();
    }

    private static ToggleButton Toggle(Window shell) =>
        Audit.Descendants(shell).OfType<ToggleButton>().Single(b => b.Name == "BlurMode");

    private static TextBlock Status(Window shell) =>
        Audit.Descendants(shell).OfType<TextBlock>().Single(b => b.Name == "BlurBoxStatus");

    /// <summary>Within a pixel: the window rounds layout to whole pixels (Window.Base), and the picture's place is rounded too.</summary>
    private static bool Near(double a, double b) => Math.Abs(a - b) <= 1.01;
}
