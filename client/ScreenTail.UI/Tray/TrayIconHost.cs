using System.Drawing;
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
/// The icons are the brand's tray glyphs (Spec §2.0, tray glyph tiers), built from
/// <c>shared/design/brand/tray-*.svg</c> by <c>build-icons.py</c> and embedded here, one .ico per state and
/// taskbar with a frame for each scale from 100% to 300%. The states differ by shape as well as by colour
/// (Spec §7): a technician with a colour-vision deficiency, and a technician glancing at a 16-pixel glyph,
/// both have to be able to tell recording (a dot) from paused (two bars) from offline (a cross). It is
/// also the one indicator that cannot be dismissed (the HUD can be hidden between sessions, the tray icon
/// cannot), which is why the grey "unknown" state is its own icon rather than falling back to idle.
///
/// The glyph follows the taskbar: Iris on a light one, Ribbon 400 on a dark one, re-read whenever Windows
/// says a user preference changed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconHost : IDisposable
{
    /// <summary>The thread that built the icon, which is the one that may touch it (the shell's UI thread).</summary>
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private readonly Forms.NotifyIcon _icon = new() { Visible = false };
    private readonly Dictionary<(TrayIcon State, bool Light), Icon> _icons = [];
    private readonly Func<bool> _readLightTaskbar;
    private readonly Forms.ToolStripMenuItem _stateLine;
    private readonly Dictionary<Forms.ToolStripMenuItem, Forms.Padding> _padding = [];
    private TrayPresence? _shown;
    private NotificationAction? _pending;
    private bool _lightTaskbar;

    public TrayIconHost()
        : this(ReadLightTaskbar)
    {
    }

    /// <param name="readLightTaskbar">Whether the taskbar is light now; a test passes its own.</param>
    internal TrayIconHost(Func<bool> readLightTaskbar)
    {
        _readLightTaskbar = readLightTaskbar;
        _lightTaskbar = readLightTaskbar();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
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
        menu.Items.Add(Item("Set up ScreenTail…", () => Setup?.Invoke()));
        menu.Items.Add(Item("Hide the recording pill (this session)", () => HidePill?.Invoke()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Quit", () => Quit?.Invoke()));

        // Spec §7's 28 px target, at whatever scale the menu is drawn: fitted when the menu gets its window
        // and every time it opens, since only then is its DPI known (ST-084: a fixed padding worked out at
        // construction came to 25.6 px on the laptop's scaled screen).
        foreach (var item in menu.Items.OfType<Forms.ToolStripMenuItem>())
        {
            _padding[item] = item.Padding;
        }

        menu.HandleCreated += (_, _) => FitItems(menu);
        menu.Opening += (_, _) => FitItems(menu);
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

    /// <summary>The quick menu (Spec §5 S1), for the accessibility suite's checks of its names and sizes (ST-084).</summary>
    internal Forms.ContextMenuStrip Menu => _icon.ContextMenuStrip!;

    /// <summary>The icon's tooltip as Windows holds it, for the tests that watch the clock move (ST-072).</summary>
    internal string Tooltip => _icon.Text;

    /// <summary>The file name of the icon on show, for the tests of the state and taskbar pick.</summary>
    internal string? ShownIcon { get; private set; }

    /// <summary>The menu's first row as drawn, for the same tests.</summary>
    internal string StateLine => _stateLine.Text ?? string.Empty;

    public event Action? Pause;

    public event Action? Stop;

    public event Action? Discard;

    public event Action? ShowDiagnostics;

    public event Action? Open;

    /// <summary>Spec §5 S8: the onboarding wizard, re-runnable from here (ST-083).</summary>
    public event Action? Setup;

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
        if (!Disposed)
        {
            _icon.Visible = true;
        }
    }

    /// <summary>
    /// Repaints from the shell's store. Safe to call from any thread: the icon is a Windows Forms
    /// component and has to be touched on the UI thread, so it marshals rather than trusting the caller.
    /// </summary>
    public void Update(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => Update(snapshot));
            return;
        }

        // A change queued from the pipe's thread can run after Quit disposed the icon, and repainting a
        // disposed icon throws on the UI thread.
        if (Disposed)
        {
            return;
        }

        var presence = TrayPresence.From(snapshot);
        if (presence == _shown)
        {
            return;
        }

        _shown = presence;
        ShowIcon(presence.Icon);

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
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => Notify(notice));
            return;
        }

        if (Disposed)
        {
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

    /// <summary>Whether <see cref="Dispose"/> has run, for the shell's quit tests.</summary>
    internal bool Disposed { get; private set; }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
    }

    /// <summary>
    /// Re-reads the taskbar's theme and, when it changed, repaints the icon on show. Windows raises this
    /// for any preference change, on its own thread, so it marshals and compares rather than repainting.
    /// </summary>
    internal void RefreshTheme()
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(RefreshTheme);
            return;
        }

        if (Disposed)
        {
            return;
        }

        var light = _readLightTaskbar();
        if (light == _lightTaskbar)
        {
            return;
        }

        _lightTaskbar = light;
        if (_shown is { } shown)
        {
            ShowIcon(shown.Icon);
        }
    }

    /// <summary>
    /// One state's icon at this screen's small-icon size, from the embedded .ico: 16 px at 100%, 20 at
    /// 125%, 24 at 150%, 32 at 200%, and so on, so Windows never scales a 16-pixel glyph up.
    /// </summary>
    internal static Icon Load(TrayIcon state, bool lightTaskbar, System.Drawing.Size size)
    {
        var name = ResourcePrefix + TrayGlyph.FileName(state, lightTaskbar);
        using var stream = typeof(TrayIconHost).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The tray icon {name} is not embedded.");
        return new Icon(stream, size);
    }

    private const string ResourcePrefix = "ScreenTail.UI.Assets.Tray.";

    /// <summary>
    /// Whether the taskbar is light. Any failure to read it is the default dark taskbar: the icon is
    /// still shown, in the glyph that is legible on most taskbars, rather than not at all.
    /// </summary>
    private static bool ReadLightTaskbar()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return TrayGlyph.IsLightTaskbar(key?.GetValue("SystemUsesLightTheme"));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.VisualStyle or Microsoft.Win32.UserPreferenceCategory.Color)
        {
            RefreshTheme();
        }
    }

    /// <summary>
    /// Pads each item to at least 28 device-independent pixels at the menu's DPI: back to its own padding,
    /// laid out, then whatever it lacks added above and below.
    /// </summary>
    private void FitItems(Forms.ContextMenuStrip menu) => FitItems(menu, menu.DeviceDpi);

    /// <param name="deviceDpi">The DPI the menu is drawn at; a test passes a scaled one the hosted runner's screen does not have.</param>
    internal void FitItems(Forms.ContextMenuStrip menu, int deviceDpi)
    {
        ArgumentNullException.ThrowIfNull(menu);
        var floor = (int)Math.Ceiling(28 * deviceDpi / 96.0);
        foreach (var (item, padding) in _padding)
        {
            item.Padding = padding;
        }

        menu.PerformLayout();
        foreach (var (item, padding) in _padding)
        {
            var lacking = Math.Max(0, floor - item.Height);
            var above = lacking / 2;
            item.Padding = new Forms.Padding(padding.Left, padding.Top + above, padding.Right, padding.Bottom + lacking - above);
        }

        menu.PerformLayout();
    }

    private static Forms.ToolStripMenuItem Item(string text, Action invoke)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => invoke();
        return item;
    }

    private void ShowIcon(TrayIcon state)
    {
        var key = (state, _lightTaskbar);
        if (!_icons.TryGetValue(key, out var icon))
        {
            icon = Load(state, _lightTaskbar, Forms.SystemInformation.SmallIconSize);
            _icons[key] = icon;
        }

        _icon.Icon = icon;
        ShownIcon = TrayGlyph.FileName(state, _lightTaskbar);
    }
}
