using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Windows;
using ScreenTail.Core.Notifications;
using ScreenTail.Core.Shell;
using Forms = System.Windows.Forms;

namespace ScreenTail.UI.Tray;

/// <summary>
/// The tray icon (ST-071, ST-085), and the first thing in this product that makes INV-4 true in the
/// running application.
///
/// Until this existed there was no capture indicator anywhere: <see cref="TrayPresence"/> computed what an
/// icon would say and had no callers, and the HUD was built by a screenshot harness and never shown
/// (weaknesses P0-2). INV-4 — capture is always visibly indicated, and there is no silent-capture mode —
/// is the invariant that makes the product defensible, and it was enforced by nothing.
///
/// The icon is drawn rather than shipped as a resource so that the five states differ by shape as well as
/// by colour (Spec §7): a technician with a colour-vision deficiency, and a technician glancing at a
/// 16-pixel glyph, both have to be able to tell recording from paused. It is also the one indicator that
/// cannot be dismissed — the HUD can be hidden between sessions, the tray icon cannot — which is why the
/// grey "unknown" state is its own icon rather than falling back to idle.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconHost : IDisposable
{
    private readonly Forms.NotifyIcon _icon = new() { Visible = false };
    private readonly Dictionary<TrayIcon, Icon> _icons = [];
    private readonly List<IntPtr> _handles = [];
    private readonly Forms.ToolStripMenuItem _stateLine;
    private TrayPresence? _shown;
    private NotificationAction? _pending;

    public TrayIconHost()
    {
        _stateLine = new Forms.ToolStripMenuItem("Connecting…") { Enabled = false };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_stateLine);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Pause capture", () => Pause?.Invoke()));
        menu.Items.Add(Item("Stop and draft", () => Stop?.Invoke()));
        menu.Items.Add(Item("Discard session", () => Discard?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("What's being captured?", () => ShowDiagnostics?.Invoke()));
        menu.Items.Add(Item("Open ScreenTail", () => Open?.Invoke()));
        menu.Items.Add(Item("Hide the recording pill (this session)", () => HidePill?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Quit", () => Quit?.Invoke()));
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => Open?.Invoke();
        _icon.BalloonTipClicked += (_, _) =>
        {
            // Spec §6 gives every message at most one action, so there is only ever one thing this can
            // mean. None means the notification was informational and pressing it opens nothing.
            if (_pending is { } action && action != NotificationAction.None)
            {
                NotificationClicked?.Invoke(action);
            }
        };
    }

    public event Action? Pause;

    public event Action? Stop;

    public event Action? Discard;

    public event Action? ShowDiagnostics;

    public event Action? Open;

    /// <summary>
    /// The technician asking for a tray-only session. INV-4 permits it only as an explicit choice, and
    /// only for this session — which is why it is a menu item here and not a setting.
    /// </summary>
    public event Action? HidePill;

    public event Action? Quit;

    /// <summary>The technician pressed the notification. Carries what it was about.</summary>
    public event Action<NotificationAction>? NotificationClicked;

    /// <summary>Shows the icon. Until this is called nothing appears, which is only right before startup finishes.</summary>
    public void Show(ShellSnapshot snapshot)
    {
        Update(snapshot);
        _icon.Visible = true;
    }

    /// <summary>
    /// Repaints from the shell's store. Safe to call from any thread: the icon is a Windows Forms
    /// component and has to be touched on the UI thread, so it marshals rather than trusting the caller.
    /// </summary>
    public void Update(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => Update(snapshot));
            return;
        }

        var presence = TrayPresence.From(snapshot);
        if (presence == _shown)
        {
            return;
        }

        _shown = presence;
        _icon.Icon = IconFor(presence.Icon);

        // Windows truncates a tooltip past 63 characters, and a truncated state is a misread state.
        _icon.Text = presence.Tooltip.Length <= 63 ? presence.Tooltip : presence.Tooltip[..60] + "...";
        _stateLine.Text = presence.StateLine;
    }

    /// <summary>
    /// Says one thing, through Windows' own notification path (ST-073).
    ///
    /// A balloon on the tray icon rather than a window of our own, and that is the whole of "Focus Assist
    /// respected": Windows decides whether to show it, so a technician with Do Not Disturb on — presenting,
    /// on a call, mid-demo — is not interrupted by us, and we do not have to detect a setting that has
    /// changed name twice. A custom toast window would have to ask, and would get it wrong the first time
    /// Microsoft renamed it again.
    /// </summary>
    public void Notify(Notification notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => Notify(notice));
            return;
        }

        _pending = notice.Action;
        _icon.BalloonTipTitle = "ScreenTail";
        _icon.BalloonTipText = notice.Text;
        _icon.BalloonTipIcon = Forms.ToolTipIcon.None;

        // Ten seconds is a hint; Windows applies its own accessibility timeout, which is longer for
        // people who have asked for longer.
        _icon.ShowBalloonTip(10_000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        foreach (var handle in _handles)
        {
            _ = DestroyIcon(handle);
        }

        _icons.Clear();
        _handles.Clear();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Forms.ToolStripMenuItem Item(string text, Action invoke)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => invoke();
        return item;
    }

    private Icon IconFor(TrayIcon state)
    {
        if (_icons.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var icon = Draw(state, out var handle);
        _handles.Add(handle);
        _icons[state] = icon;
        return icon;
    }

    /// <summary>
    /// Draws one 16x16 glyph. Shape first, colour second: a filled circle is recording, two bars are
    /// paused, a hollow ring is idle, a square is a draft waiting, and a hollow ring with a gap is the
    /// state nobody knows.
    /// </summary>
    private static Icon Draw(TrayIcon state, out IntPtr handle)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var colour = state switch
            {
                TrayIcon.Recording => Color.FromArgb(0xE5, 0x5B, 0x5B),
                TrayIcon.Paused => Color.FromArgb(0xE8, 0xB3, 0x39),
                TrayIcon.DraftReady => Color.FromArgb(0x4F, 0x8C, 0xFF),
                TrayIcon.Offline => Color.FromArgb(0x94, 0xA3, 0xB8),
                _ => Color.FromArgb(0xB6, 0xC2, 0xD1),
            };

            using var brush = new SolidBrush(colour);
            using var pen = new Pen(colour, 2f);
            switch (state)
            {
                case TrayIcon.Recording:
                    g.FillEllipse(brush, 3, 3, 10, 10);
                    break;
                case TrayIcon.Paused:
                    g.FillRectangle(brush, 4, 3, 3, 10);
                    g.FillRectangle(brush, 9, 3, 3, 10);
                    break;
                case TrayIcon.DraftReady:
                    g.FillRectangle(brush, 3, 3, 10, 10);
                    break;
                case TrayIcon.Offline:
                    // A ring with a gap: the shape says "incomplete" before the colour says "grey".
                    g.DrawArc(pen, 3, 3, 10, 10, 45, 270);
                    break;
                default:
                    g.DrawEllipse(pen, 3, 3, 10, 10);
                    break;
            }
        }

        // FromHandle does not own the handle, so it is kept and destroyed in Dispose. Without that this
        // leaks one GDI icon per state for the life of the process.
        handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }
}
