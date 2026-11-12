using System.Runtime.Versioning;
using System.Windows.Threading;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Tray;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>
/// The tray icon after Quit (ST-071; 2026-10-12 review, S-47).
///
/// The shell's state changes reach the icon from the pipe's thread, so the icon queues them on its UI
/// thread. A change queued just before Quit ran after the icon was disposed, and repainting a disposed
/// Windows Forms icon throws on the UI thread: the first hosted run of the shell's quit test ended there,
/// the same crash on Quit the shell's fix was for.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconHostTests
{
    [Fact]
    public void AChangeQueuedBeforeQuitDoesNothingOnceTheIconIsGone()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        StaThread.Run(async () =>
        {
            var tray = new TrayIconHost();
            tray.Show(Snapshot("idle"));

            // From another thread, as the shell's state changes arrive: queued on this one.
            await Task.Run(() => tray.Update(Snapshot("recording")), TestContext.Current.CancellationToken);
            tray.Dispose();

            // The queued repaint runs now, after Quit. Then the same from this thread, and a second Quit.
            await Dispatcher.Yield(DispatcherPriority.Background);
            tray.Update(Snapshot("paused"));
            tray.Show(Snapshot("recording"));
            tray.Dispose();

            Assert.True(tray.Disposed);
        });
    }

    /// <summary>
    /// ST-016: each tray state is its own brand icon on each taskbar, at the size asked for. INV-4 rests on
    /// recording, paused, offline and the rest never coming out as the same picture.
    /// </summary>
    [Fact]
    public void EachStateLoadsADistinctIconOnBothTaskbars()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        foreach (var size in new[] { 16, 24, 32 })
        {
            var pictures = new HashSet<string>(StringComparer.Ordinal);
            foreach (var light in new[] { true, false })
            {
                foreach (var state in Enum.GetValues<TrayIcon>())
                {
                    using var icon = TrayIconHost.Load(state, light, new System.Drawing.Size(size, size));
                    using var bitmap = icon.ToBitmap();
                    Assert.Equal(size, bitmap.Width);
                    Assert.Equal(size, bitmap.Height);
                    Assert.True(pictures.Add(Pixels(bitmap)), $"{state} on the {(light ? "light" : "dark")} taskbar at {size} px looks like another state.");
                }
            }
        }
    }

    /// <summary>ST-016: the glyph follows the taskbar, at start and when Windows says the theme changed.</summary>
    [Fact]
    public void TheTaskbarThemePicksTheGlyph()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        StaThread.Run(() =>
        {
            var light = true;
            using var tray = new TrayIconHost(() => light);
            tray.Show(Snapshot("idle"));
            Assert.Equal("tray-idle-light.ico", tray.ShownIcon);

            light = false;
            tray.RefreshTheme();
            Assert.Equal("tray-idle-dark.ico", tray.ShownIcon);

            tray.Update(Snapshot("recording"));
            Assert.Equal("tray-recording-dark.ico", tray.ShownIcon);
            tray.Update(Snapshot("paused"));
            Assert.Equal("tray-paused-dark.ico", tray.ShownIcon);
            tray.Update(new ShellSnapshot(ServiceConnection.Unavailable, null, ShellView.Review, null));
            Assert.Equal("tray-offline-dark.ico", tray.ShownIcon);

            light = true;
            tray.RefreshTheme();
            Assert.Equal("tray-offline-light.ico", tray.ShownIcon);
            return Task.CompletedTask;
        });
    }

    private static string Pixels(System.Drawing.Bitmap bitmap)
    {
        var text = new System.Text.StringBuilder();
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                _ = text.Append(bitmap.GetPixel(x, y).ToArgb().ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private static ShellSnapshot Snapshot(string state) =>
        new(ServiceConnection.Connected, new CaptureStateSnapshot { State = state, SessionId = "s1", RemoteTool = "rdp", ElapsedMs = 60_000 }, ShellView.Review, null);
}
