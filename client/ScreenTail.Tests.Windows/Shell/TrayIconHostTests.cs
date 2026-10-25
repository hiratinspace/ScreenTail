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

    private static ShellSnapshot Snapshot(string state) =>
        new(ServiceConnection.Connected, new CaptureStateSnapshot { State = state, SessionId = "s1", RemoteTool = "rdp", ElapsedMs = 60_000 }, ShellView.Review, null);
}
