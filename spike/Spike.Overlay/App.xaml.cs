using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace ScreenTail.Spike.Overlay;

/// <summary>Stand-in for the UI process: a tray icon plus a capture-excluded, non-activating overlay.</summary>
public partial class App : Application
{
    private NotifyIcon? _tray;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // --visible starts in the control case (no exclusion) so CI can produce it without the tray menu.
        var excludeAtStart = !e.Args.Contains("--visible");
        var overlay = new OverlayWindow(excludeAtStart);
        overlay.Show();

        // Unchecking produces the control case: the capture check must then report CAPTURED.
        var exclude = new ToolStripMenuItem("Exclude overlay from capture") { Checked = excludeAtStart, CheckOnClick = true };
        exclude.CheckedChanged += (_, _) => overlay.SetExcludedFromCapture(exclude.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(exclude);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Shutdown());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "ScreenTail spike overlay",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        base.OnExit(e);
    }
}
