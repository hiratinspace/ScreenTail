using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;

namespace ScreenTail.UI.Hud;

/// <summary>
/// The recording pill (ST-072, Spec §5 S2).
///
/// Three properties here are not cosmetic, and each is enforced by an interop call rather than by a XAML
/// attribute that looks like it would do it:
///
/// <list type="bullet">
/// <item><b>It never takes focus.</b> A pill that steals the foreground from the remote-tool window mid-
/// session interrupts the work it is documenting, and on a customer's machine the technician may not
/// even realise where their keystrokes went.</item>
/// <item><b>It never appears in a capture.</b> Not in ours — a screenshot of our own HUD tells a reader
/// nothing — and not in the customer's screen share either. That last one is why it can stay visible at
/// all times (v0.4.1 Q1), which is what lets INV-4 hold without an auto-hide.</item>
/// <item><b>It stays on top.</b> An indicator behind the window it is indicating about is not an
/// indicator.</item>
/// </list>
/// </summary>
public partial class HudWindow : Window
{
    private readonly ShellPreferencesStore? _preferences;

    public HudWindow()
        : this(null)
    {
    }

    public HudWindow(ShellPreferencesStore? preferences)
    {
        _preferences = preferences;
        InitializeComponent();

        if (_preferences?.Load() is { HudX: > 0, HudY: > 0 } saved)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.HudX;
            Top = saved.HudY;

            // ...but only if that place still exists. Checked on load rather than here, because the
            // pill has no width until then and the question is about where its middle lands.
            Loaded += (_, _) => DockIfNowhere();
        }
        else
        {
            // Spec §5 S2 docks it to the top-right by default. Done on load, once the pill has a width.
            Loaded += (_, _) => DockTopRight();
        }

        SourceInitialized += OnSourceInitialized;
    }

    private HudViewModel? Model => DataContext as HudViewModel;

    /// <summary>
    /// What the state glyph is actually painted in, for the render harness to report when its colour
    /// assertion fails. Three attempts at this bug were spent theorising about why the brush was not
    /// applied; asking the element is quicker than any of them.
    /// </summary>
    public string GlyphForegroundDescription => StateGlyphText.Foreground switch
    {
        System.Windows.Media.SolidColorBrush solid => solid.Color.ToString(System.Globalization.CultureInfo.InvariantCulture),
        null => "(null)",
        var other => other.GetType().Name,
    };

    /// <summary>
    /// Applies the two window styles that cannot be set from XAML, as soon as there is a handle.
    ///
    /// <c>WS_EX_NOACTIVATE</c> has to be on the handle before the window is shown: <c>ShowActivated</c>
    /// only covers the first show, and a later click on the pill would otherwise activate it and pull the
    /// foreground off the remote window.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        var style = GetWindowLong(handle, GWL_EXSTYLE);
        _ = SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        // WDA_EXCLUDEFROMCAPTURE needs Windows 10 2004 or later; on anything older it fails and the call
        // returns false. Not fatal, and deliberately not silent: a HUD that shows up in a customer's
        // screen share is a privacy problem, and the diagnostics panel is where a technician would look.
        ExcludedFromCapture = SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>
    /// Whether Windows agreed to keep the pill out of screen captures. Read by the diagnostics panel:
    /// if this is false the technician is sharing their screen with our HUD on it.
    /// </summary>
    public bool ExcludedFromCapture { get; private set; }

    /// <summary>
    /// Brings the pill back where it can be seen, if the screen it was on has gone (INV-4).
    ///
    /// A technician who docks the pill on a second monitor and later unplugs it used to get a pill drawn
    /// into empty coordinate space: no indicator anywhere, while the capture service went on treating an
    /// attached UI as proof that something on screen said so (2026-09-20 review).
    ///
    /// The saved position is deliberately left alone. Plug the monitor back in and the pill goes back to
    /// where it was put, which is what somebody who arranged it that way would expect; overwriting it
    /// here would quietly lose that arrangement the first time a cable came out.
    /// </summary>
    private void DockIfNowhere()
    {
        // The bounding box of every monitor rather than the monitors themselves, which WPF does not
        // offer without dragging in WinForms. It catches the cases that happen — a monitor unplugged, a
        // coordinate nobody could have dragged to — and misses only a point in the notch of an L-shaped
        // arrangement, which draws a pill that is at worst on the wrong screen rather than on none.
        var virtualScreen = new ScreenArea(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight);

        if (!HudPlacement.IsOnScreen(Left, Top, ActualWidth, ActualHeight, [virtualScreen]))
        {
            DockTopRight();
        }
    }

    private void DockTopRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 24;
        Top = area.Top + 24;
    }

    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-click opens Review for the running session, read-only until it is stopped.
            Model?.ReviewCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // A drag and a click start identically, so the pill expands on mouse *up* without a drag. DragMove
        // blocks until the button is released and tells us nothing, so the distance is measured here.
        var from = PointToScreen(e.GetPosition(this));
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was already up. Nothing to drag, and nothing to report.
        }

        var to = PointToScreen(Mouse.GetPosition(this));
        if (Math.Abs(to.X - from.X) < 4 && Math.Abs(to.Y - from.Y) < 4)
        {
            Model?.ToggleExpandedCommand.Execute(null);
        }
        else
        {
            SavePosition();
        }
    }

    private void OnPillRightClick(object sender, MouseButtonEventArgs e)
    {
        // Hides the pill for this session only, and HudState refuses while anything is being captured.
        // The tray icon stays either way, so there is never a session with no indicator at all (INV-4).
        Model?.HideCommand.Execute(null);
        e.Handled = true;
    }

    private void SavePosition()
    {
        if (_preferences is null)
        {
            return;
        }

        var current = _preferences.Load();
        _ = _preferences.Save(current with { HudX = Left, HudY = Top });
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    private static int GetWindowLong(IntPtr window, int index) => (int)GetWindowLongPtr(window, index);

    private static IntPtr SetWindowLong(IntPtr window, int index, int value) =>
        SetWindowLongPtr(window, index, new IntPtr(value));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
