using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.Versioning;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Render;

namespace ScreenTail.UI.Tray;

/// <summary>
/// Renders the tray's quick menu (Spec §5 S1, as built v0.6.10) in each state that changes its rows, in
/// every theme, beside the pill's renders. The menu is a Windows Forms menu drawn in the app's tokens
/// (<see cref="TrayMenuRenderer"/>), so the theme the harness applies is the one these pictures show.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TrayMenuPreview
{
    /// <summary>The states whose menus differ, by name. Process names and tool names only (INV-10).</summary>
    internal static IEnumerable<(string Name, ShellSnapshot Snapshot, bool PillHidden, int Selected)> Variants()
    {
        yield return ("recording", Connected(new CaptureStateSnapshot { State = CaptureStates.Recording, SessionId = "preview", RemoteTool = "screenconnect", ElapsedMs = 761_000 }), false, 1);
        yield return ("paused", Connected(new CaptureStateSnapshot { State = CaptureStates.Paused, SessionId = "preview", RemoteTool = "screenconnect", ElapsedMs = 761_000, Reason = CaptureReasons.User }), false, -1);
        yield return ("idle-drafts", Connected(new CaptureStateSnapshot { State = CaptureStates.Idle, DraftsReady = 2, PublishesUnconfirmed = 1 }), false, 3);
        yield return ("idle-pill-hidden", Connected(new CaptureStateSnapshot { State = CaptureStates.Idle }), true, -1);
        yield return ("offline", new ShellSnapshot(ServiceConnection.Unavailable, null, ShellView.Review, null), false, -1);
    }

    public static void Capture(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        foreach (var (name, snapshot, hidden, selected) in Variants())
        {
            using var tray = new TrayIconHost(() => false);
            tray.Update(snapshot);
            tray.SetPillHidden(hidden);
            foreach (var theme in RenderThemes.All)
            {
                RenderThemeApplier.Apply(theme, System.Windows.Application.Current.Resources);
                tray.PrepareToDraw();
                var path = Path.Combine(directory, RenderThemes.FileName($"tray-menu-{name}", theme));
                Draw(tray, selected, path);
                Console.WriteLine($"  tray-menu-{name}/{theme}: {tray.Menu.Items.Count} rows, {tray.Menu.Width}x{tray.Menu.Height}");
            }
        }
    }

    /// <summary>
    /// Draws the menu as it is shown: opened off to one side so a row can be selected (the pointer's or
    /// the arrow keys' row), drawn, then closed. Fails when nothing but one colour came out.
    /// </summary>
    private static void Draw(TrayIconHost tray, int selected, string path)
    {
        var menu = tray.Menu;
        menu.Show(new Point(0, 0));
        try
        {
            if (selected >= 0 && selected < menu.Items.Count)
            {
                menu.Items[selected].Select();
            }

            menu.PerformLayout();
            using var bitmap = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
            var colours = new HashSet<int>();
            for (var y = 0; y < bitmap.Height; y += 2)
            {
                for (var x = 0; x < bitmap.Width; x += 2)
                {
                    _ = colours.Add(bitmap.GetPixel(x, y).ToArgb());
                }
            }

            if (colours.Count < 3)
            {
                throw new InvalidOperationException($"{Path.GetFileName(path)} came out blank ({colours.Count} colours).");
            }

            bitmap.Save(path, ImageFormat.Png);
        }
        finally
        {
            menu.Close();
        }
    }

    private static ShellSnapshot Connected(CaptureStateSnapshot capture) => new(ServiceConnection.Connected, capture, ShellView.Review, null);
}
