using System.Runtime.Versioning;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using ScreenTail.UI.Review;
using static ScreenTail.Tests.Windows.Review.ReviewSessions;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// The filmstrip's chords (ST-075, Spec §5 S3; 2026-10-12 review T-167). The strip is a ListBox, and a
/// ListBox's own key handler takes Space for selection and marks it handled before any KeyDown handler
/// on the ListBox runs. So Space never excluded a frame, and there was no other control that did. The
/// view listens on PreviewKeyDown, which tunnels in first.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FilmstripKeysTests
{
    [Fact]
    public void SpaceOnASelectedFrameExcludesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var frames = new RecordingFrames();
            var strip = new FilmstripViewModel(Session(Frames(2)), frames);
            var list = new ListBox { ItemsSource = strip.Items };
            var reachedKeyDown = false;
            list.KeyDown += (_, e) => reachedKeyDown |= e.Key == Key.Space;
            list.PreviewKeyDown += (_, e) => FilmstripKeys.Handle(strip, list.SelectedItem as FrameItem, e, () => { });
            var window = UiThread.Shown(list);
            try
            {
                var first = strip.Items.OfType<FrameItem>().First();
                list.SelectedItem = first;
                window.UpdateLayout();
                var cell = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(first));

                Assert.True(UiThread.Press(cell, Key.Space));
                await System.Windows.Threading.Dispatcher.Yield();

                Assert.False(first.Included);
                Assert.Equal("excluded", first.StateLabel);
                Assert.Equal([("f1", false)], frames.Included);
                Assert.Equal(["f2"], strip.IncludedFrameIds);
                Assert.False(reachedKeyDown, "the ListBox took Space before a KeyDown handler could see it; the strip has to listen on PreviewKeyDown");

                // The other chords go the same way.
                Assert.True(UiThread.Press(cell, Key.Space));
                await System.Windows.Threading.Dispatcher.Yield();
                Assert.True(first.Included);
                Assert.True(UiThread.Press(cell, Key.Delete));
                await System.Windows.Threading.Dispatcher.Yield();
                Assert.DoesNotContain(first, strip.Items);
                Assert.True(strip.HasUndoToast);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheStripListensBeforeTheListBoxDoes()
    {
        // The wiring itself, as FilmstripView.xaml declares it: PreviewKeyDown, and no KeyDown that would
        // quietly lose Space again. The XAML is copied beside the tests at build.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Review", "Xaml", "FilmstripView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var strip = Assert.Single(xaml.Descendants().Where(e => e.Name.LocalName == "ListBox" && (string?)e.Attribute(x + "Name") == "Strip"));

        Assert.Equal("OnStripKeyDown", (string?)strip.Attribute("PreviewKeyDown"));
        Assert.Null(strip.Attribute("KeyDown"));
    }

    [Theory]
    [InlineData(Key.Escape, ModifierKeys.None, true, true, FilmstripKey.CloseEnlarged)]
    [InlineData(Key.Escape, ModifierKeys.None, false, true, FilmstripKey.None)]
    [InlineData(Key.T, ModifierKeys.None, true, false, FilmstripKey.ToggleOcrText)]
    [InlineData(Key.T, ModifierKeys.None, false, true, FilmstripKey.None)]
    [InlineData(Key.Space, ModifierKeys.None, false, true, FilmstripKey.ToggleInclude)]
    [InlineData(Key.Space, ModifierKeys.None, false, false, FilmstripKey.None)]
    [InlineData(Key.Enter, ModifierKeys.None, false, true, FilmstripKey.Enlarge)]
    [InlineData(Key.Delete, ModifierKeys.None, false, true, FilmstripKey.Delete)]
    [InlineData(Key.B, ModifierKeys.None, true, true, FilmstripKey.ToggleBlur)]
    [InlineData(Key.B, ModifierKeys.None, false, true, FilmstripKey.None)]
    [InlineData(Key.Z, ModifierKeys.Control, false, true, FilmstripKey.Undo)]
    [InlineData(Key.Z, ModifierKeys.None, false, true, FilmstripKey.None)]
    public void EveryChordRoutesToOneThing(Key key, ModifierKeys modifiers, bool enlarged, bool frameSelected, FilmstripKey expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        Assert.Equal(expected, FilmstripKeys.Route(key, modifiers, enlarged, frameSelected));
    }
}
