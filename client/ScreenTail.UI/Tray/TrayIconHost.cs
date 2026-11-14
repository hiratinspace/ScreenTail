using System.Drawing;
using System.Runtime.Versioning;
using ScreenTail.Core.Notifications;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using Forms = System.Windows.Forms;

namespace ScreenTail.UI.Tray;

/// <summary>
/// The tray icon (ST-071, ST-085), and the first thing in this product that makes INV-4 true in the
/// running application.
///
/// Until this existed there was no capture indicator anywhere: <see cref="TrayPresence"/> computed what an
/// icon would say and had no callers, and the HUD was built by a screenshot harness and never shown
/// (weaknesses P0-2). INV-4 (capture is always visibly indicated, and there is no silent-capture mode)
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
/// says a user preference changed; in high contrast, the taskbar's window colour picks it.
///
/// The quick menu is <see cref="TrayMenu"/>'s rows (Spec §5 S1, as built v0.6.10), drawn in the app's
/// tokens by <see cref="TrayMenuRenderer"/>, with the state line carrying the same glyph as the icon.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconHost : IDisposable
{
    /// <summary>The thread that built the icon, which is the one that may touch it (the shell's UI thread).</summary>
    private readonly System.Windows.Threading.Dispatcher _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private readonly Forms.NotifyIcon _icon = new() { Visible = false };
    private readonly Dictionary<(TrayIcon State, bool Light), Icon> _icons = [];
    private readonly Func<bool> _readLightTaskbar;
    private readonly Forms.ContextMenuStrip _menu;

    /// <summary>The padding every menu item is made with, which <see cref="FitItems(Forms.ContextMenuStrip, int)"/> adds to.</summary>
    private readonly Forms.Padding _padding;
    private IReadOnlyList<TrayRow> _rows = [];
    private ShellSnapshot _snapshot = new(ServiceConnection.Connecting, null, ShellView.Review, null);
    private TrayPresence? _shown;
    private NotificationAction? _pending;
    private bool _lightTaskbar;
    private bool _pillHidden;
    private Bitmap? _stateGlyph;
    private Font? _menuFont;
    private Font? _stateFont;

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

        // Spec §5 S1 drawn in the app's tokens (TrayMenuRenderer), with the rows TrayMenu decides.
        _menu = new Forms.ContextMenuStrip { Renderer = new TrayMenuRenderer(), ShowItemToolTips = false };
        using (var probe = new Forms.ToolStripMenuItem())
        {
            _padding = probe.Padding;
        }

        ApplyRows(TrayMenu.For(_snapshot, _pillHidden), TrayPresence.From(_snapshot));

        // Spec §7's 28 px target, at whatever scale the menu is drawn: fitted when the menu gets its window
        // and every time it opens, since only then is its DPI known (ST-084: a fixed padding worked out at
        // construction came to 25.6 px on the laptop's scaled screen). The font and the state line's glyph
        // are taken again on opening too, so a theme changed since the last time is the one drawn.
        _menu.HandleCreated += (_, _) => FitItems(_menu);
        _menu.Opening += (_, _) =>
        {
            Restyle();
            FitItems(_menu);
        };
        _icon.ContextMenuStrip = _menu;
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
    internal Forms.ContextMenuStrip Menu => _menu;

    /// <summary>The rows on show, for the tests that hold the menu to them.</summary>
    internal IReadOnlyList<TrayRow> Rows => _rows;

    /// <summary>The icon's tooltip as Windows holds it, for the tests that watch the clock move (ST-072).</summary>
    internal string Tooltip => _icon.Text;

    /// <summary>The file name of the icon on show, for the tests of the state and taskbar pick.</summary>
    internal string? ShownIcon { get; private set; }

    /// <summary>The menu's first row as drawn, for the same tests.</summary>
    internal string StateLine => _menu.Items.Count > 0 ? _menu.Items[0].Text ?? string.Empty : string.Empty;

    public event Action? Pause;

    public event Action? Resume;

    public event Action? Stop;

    public event Action? Discard;

    public event Action? ShowDiagnostics;

    /// <summary>The icon double-clicked: the shell, as it was left.</summary>
    public event Action? Open;

    /// <summary>"Open last draft (N ready)": the shell, on Review of the newest draft.</summary>
    public event Action? OpenLastDraft;

    /// <summary>"Session history": the shell, on All sessions.</summary>
    public event Action? History;

    public event Action? Settings;

    /// <summary>Spec §5 S8: the onboarding wizard, re-runnable from here (ST-083).</summary>
    public event Action? Setup;

    /// <summary>
    /// The technician asking for a tray-only stretch between sessions. INV-4 permits it only as an
    /// explicit choice, only while capture is known to be off, and only until the next session starts,
    /// which is why it is a menu item here and not a setting, and why <see cref="TrayMenu"/> disables it
    /// while a session may be under way.
    /// </summary>
    public event Action? HidePill;

    /// <summary>The pill brought back before the next session would have brought it.</summary>
    public event Action? ShowPill;

    public event Action? Quit;

    /// <summary>The technician pressed the notification. Carries what it was about.</summary>
    public event Action<NotificationAction>? NotificationClicked;

    /// <summary>
    /// Whether the technician has put the pill away, so the menu offers to bring it back. The shell says
    /// so each time it changes; the menu is redrawn from the last snapshot.
    /// </summary>
    public void SetPillHidden(bool hidden)
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(() => SetPillHidden(hidden));
            return;
        }

        if (Disposed || hidden == _pillHidden)
        {
            return;
        }

        _pillHidden = hidden;
        _shown = null;
        Update(_snapshot);
    }

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
        var rows = TrayMenu.For(snapshot, _pillHidden);
        _snapshot = snapshot;
        if (presence == _shown && rows.SequenceEqual(_rows))
        {
            return;
        }

        var iconChanged = _shown?.Icon != presence.Icon;
        _shown = presence;
        ShowIcon(presence.Icon);

        // Windows truncates a tooltip past 63 characters, and a truncated state is a misread state.
        _icon.Text = presence.Tooltip.Length <= 63 ? presence.Tooltip : presence.Tooltip[..60] + "...";
        ApplyRows(rows, presence);
        if (iconChanged)
        {
            SetStateGlyph(presence.Icon);
        }
    }

    /// <summary>
    /// Says one thing, through Windows' own notification path (ST-073).
    ///
    /// A balloon on the tray icon rather than a window of our own, and that is the whole of "Focus Assist
    /// respected": Windows decides whether to show it, so a technician with Do Not Disturb on (presenting,
    /// on a call, mid-demo) is not interrupted by us, and we do not have to detect a setting that has
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
        _menu.Dispose();
        _stateGlyph?.Dispose();
        _stateGlyph = null;
        _menuFont?.Dispose();
        _stateFont?.Dispose();
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
            return TrayGlyph.IsLightTaskbar(
                key?.GetValue("SystemUsesLightTheme"),
                Forms.SystemInformation.HighContrast,
                SystemColors.Window.GetBrightness() > 0.5f);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        // Accessibility is the category high contrast turning on or off arrives in.
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.VisualStyle or Microsoft.Win32.UserPreferenceCategory.Color or Microsoft.Win32.UserPreferenceCategory.Accessibility)
        {
            RefreshTheme();
        }
    }

    /// <summary>
    /// Pads each item to at least 28 device-independent pixels at the menu's DPI: back to its own padding,
    /// laid out, then whatever it lacks added above and below.
    /// </summary>
    private void FitItems(Forms.ContextMenuStrip menu) => FitItems(menu, menu.DeviceDpi);

    /// <summary>What opening the menu does before it is drawn, for the render harness, which draws it unopened.</summary>
    internal void PrepareToDraw()
    {
        _ = _menu.Handle;
        Restyle();
        FitItems(_menu);
    }

    /// <param name="deviceDpi">The DPI the menu is drawn at; a test passes a scaled one the hosted runner's screen does not have.</param>
    internal void FitItems(Forms.ContextMenuStrip menu, int deviceDpi)
    {
        ArgumentNullException.ThrowIfNull(menu);
        var floor = (int)Math.Ceiling(28 * deviceDpi / 96.0);
        var items = menu.Items.OfType<Forms.ToolStripMenuItem>().ToList();
        foreach (var item in items)
        {
            item.Padding = _padding;
        }

        menu.PerformLayout();
        foreach (var item in items)
        {
            var lacking = Math.Max(0, floor - item.Height);
            var above = lacking / 2;
            item.Padding = new Forms.Padding(_padding.Left, _padding.Top + above, _padding.Right, _padding.Bottom + lacking - above);
        }

        menu.PerformLayout();
    }

    /// <summary>
    /// Puts the rows on the menu. The same rows in the same places, which is every tick of the recording
    /// clock, change their words in place, so an open menu does not jump; a different set is built again.
    /// </summary>
    private void ApplyRows(IReadOnlyList<TrayRow> rows, TrayPresence presence)
    {
        var same = rows.Count == _rows.Count && rows.Zip(_rows).All(pair => pair.First.Kind == pair.Second.Kind && pair.First.Command == pair.Second.Command);
        if (!same)
        {
            var old = _menu.Items.Cast<Forms.ToolStripItem>().ToList();
            _menu.Items.Clear();
            foreach (var item in old)
            {
                if (item.Image is not null && ReferenceEquals(item.Image, _stateGlyph))
                {
                    item.Image = null;
                }

                item.Dispose();
            }

            foreach (var row in rows)
            {
                _menu.Items.Add(Build(row));
            }

            Restyle();
            if (_menu.IsHandleCreated)
            {
                FitItems(_menu);
            }
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (_menu.Items[i] is Forms.ToolStripMenuItem item)
            {
                Fill(item, rows[i]);
            }
        }

        _rows = rows;
        if (!same)
        {
            SetStateGlyph((_shown ?? presence).Icon);
        }
    }

    private Forms.ToolStripItem Build(TrayRow row)
    {
        if (row.Kind == TrayRowKind.Separator)
        {
            return new Forms.ToolStripSeparator { Tag = new TrayRowTag(row.Kind, row.Command), AccessibleRole = Forms.AccessibleRole.Separator };
        }

        var item = new Forms.ToolStripMenuItem { Tag = new TrayRowTag(row.Kind, row.Command), ImageScaling = Forms.ToolStripItemImageScaling.None };
        var command = row.Command;
        item.Click += (_, _) => Invoke(command);
        Fill(item, row);
        return item;
    }

    /// <summary>The row's words, hotkey, name for a screen reader and whether it can be chosen.</summary>
    private static void Fill(Forms.ToolStripMenuItem item, TrayRow row)
    {
        // A state line or hint has no access key, and an ampersand in it (none today) is text.
        var text = row.Kind is TrayRowKind.State or TrayRowKind.Hint ? row.Text.Replace("&", "&&", StringComparison.Ordinal) : row.Text;
        if (item.Text != text)
        {
            item.Text = text;
        }

        item.ShortcutKeyDisplayString = row.Hotkey;
        item.ShowShortcutKeys = row.Hotkey is not null;
        item.Enabled = row.Enabled;

        // Read as the words; the chord, a global hotkey rather than a menu shortcut, as its description.
        item.AccessibleName = row.Name;
        item.AccessibleDescription = row.Hotkey is { } chord ? $"Hotkey {chord}" : null;
    }

    /// <summary>The font from the tokens (font.ui at size.md, the state line semibold), taken again on each opening.</summary>
    private void Restyle()
    {
        var app = System.Windows.Application.Current;
        var size = app?.TryFindResource("Size.md") is double px ? px : 14d;
        var family = FaceName(app?.TryFindResource("Font.ui") as System.Windows.Media.FontFamily);
        var points = (float)(size * 72 / 96);
        if (_menuFont is null || !_menuFont.FontFamily.Name.Equals(family, StringComparison.OrdinalIgnoreCase) || Math.Abs(_menuFont.SizeInPoints - points) > 0.01)
        {
            var font = new Font(family, points, FontStyle.Regular);
            _menu.Font = font;
            _menuFont?.Dispose();
            _menuFont = font;
            _stateFont?.Dispose();
            _stateFont = new Font(font, FontStyle.Bold);
        }

        if (_menu.Items.Count > 0 && _menu.Items[0] is Forms.ToolStripMenuItem state && !ReferenceEquals(state.Font, _stateFont))
        {
            // Semibold where the face has it; GDI+ offers bold, the nearest it can draw.
            state.Font = _stateFont;
        }

        if (_shown is { } shown)
        {
            SetStateGlyph(shown.Icon);
        }
    }

    /// <summary>The faces installed on this machine, read once.</summary>
    private static readonly Lazy<HashSet<string>> Installed = new(() =>
    {
        using var installed = new System.Drawing.Text.InstalledFontCollection();
        return installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    });

    /// <summary>The first face in the token's list that is installed, else the system's menu font.</summary>
    private static string FaceName(System.Windows.Media.FontFamily? token)
    {
        var names = Installed.Value;
        foreach (var name in (token?.Source ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (names.Contains(name))
            {
                return name;
            }
        }

        return Forms.SystemInformation.MenuFont.FontFamily.Name;
    }

    /// <summary>
    /// The state line's glyph: the tray's own icon for the state, in the version drawn for a ground like
    /// the menu's, so the line reads with the same picture the taskbar shows (Spec §5 S1, "● Recording").
    /// </summary>
    private void SetStateGlyph(TrayIcon state)
    {
        if (_menu.Items.Count == 0 || _menu.Items[0] is not Forms.ToolStripMenuItem line)
        {
            return;
        }

        var side = (int)Math.Round(16 * _menu.DeviceDpi / 96.0);
        using var icon = Load(state, TrayMenuRenderer.LightGround, new System.Drawing.Size(side, side));
        var glyph = icon.ToBitmap();
        line.Image = glyph;
        _stateGlyph?.Dispose();
        _stateGlyph = glyph;
    }

    private void Invoke(TrayCommand command)
    {
        var handler = command switch
        {
            TrayCommand.Pause => Pause,
            TrayCommand.Resume => Resume,
            TrayCommand.Stop => Stop,
            TrayCommand.Discard => Discard,
            TrayCommand.OpenLastDraft => OpenLastDraft,
            TrayCommand.History => History,
            TrayCommand.Diagnostics => ShowDiagnostics,
            TrayCommand.HidePill => HidePill,
            TrayCommand.ShowPill => ShowPill,
            TrayCommand.Settings => Settings,
            TrayCommand.Setup => Setup,
            TrayCommand.Quit => Quit,
            _ => null,
        };
        handler?.Invoke();
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
