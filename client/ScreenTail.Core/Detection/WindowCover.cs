namespace ScreenTail.Core.Detection;

/// <summary>A rectangle on the virtual screen, in the same pixels the capturer copies. Right and bottom are exclusive.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public bool IsEmpty => Right <= Left || Bottom <= Top;

    /// <summary>Whether the two share at least one pixel. Touching edges share none.</summary>
    public bool Intersects(ScreenRect other) =>
        !IsEmpty && !other.IsEmpty
        && Left < other.Right && other.Left < Right
        && Top < other.Bottom && other.Top < Bottom;
}

/// <summary>
/// What the capturer knows about one window above the one in front, in z-order. Window properties only:
/// nothing here is read from the window's content.
/// </summary>
/// <param name="Bounds">Where it is drawn, without the invisible resize border Windows adds around it.</param>
/// <param name="Visible">Shown, and not minimized.</param>
/// <param name="Cloaked">Hidden by the compositor (a suspended app, another virtual desktop), so not on screen.</param>
/// <param name="Topmost">Kept above every ordinary window, as toasts, popups and always-on-top tools are.</param>
/// <param name="SameProcess">Owned by the process of the window in front: its own menu or toolbar.</param>
/// <param name="ExcludedFromCapture">Set to stay out of screen capture, as the HUD is, so absent from the frame anyway.</param>
/// <param name="Taskbar">The shell's taskbar on one of the monitors.</param>
public readonly record struct WindowAbove(
    ScreenRect Bounds,
    bool Visible,
    bool Cloaked,
    bool Topmost,
    bool SameProcess,
    bool ExcludedFromCapture,
    bool Taskbar);

/// <summary>
/// Whether something lies over the window in front, so that a frame copied off the screen over its rectangle
/// would hold another application's pixels (ST-025).
///
/// The capturer copies the screen, not the window, so a toast, a call notification or an always-on-top tool
/// over an in-scope window is in the frame with it. Until the grab is of the window itself (PrintWindow or
/// Windows.Graphics.Capture), a sample with another application's topmost window over it is skipped rather
/// than stored. Kept apart from the Windows plumbing so the rule is testable without a desktop.
/// </summary>
public static class WindowCover
{
    /// <summary>
    /// How many windows above the one in front are looked at before giving up. A desktop has a few dozen;
    /// a walk that has not reached the top by this many is treated as covered rather than trusted.
    /// </summary>
    public const int MaxWindowsAbove = 512;

    /// <summary>Whether this window, lying above the target in z-order, puts foreign pixels into the target's frame.</summary>
    public static bool Covers(ScreenRect target, WindowAbove above)
    {
        if (!above.Visible || above.Cloaked || !above.Topmost)
        {
            return false;
        }

        // The window in front's own menus and toolbars are part of what is being photographed, and a
        // window the compositor already leaves out of the frame cannot be in it.
        if (above.SameProcess || above.ExcludedFromCapture)
        {
            return false;
        }

        // The taskbar sits along a screen edge for the whole day. It is the shell's own strip, not another
        // application's window, and refusing every frame of a window that touches it would turn this
        // interim measure into no frames at all for an ordinary layout.
        if (above.Taskbar)
        {
            return false;
        }

        return target.Intersects(above.Bounds);
    }

    /// <summary>
    /// Whether any of the windows above the target covers it. A walk that was cut short says covered:
    /// what was not looked at may be on top.
    /// </summary>
    public static bool AnyCovers(ScreenRect target, IEnumerable<WindowAbove> above, bool walkComplete)
    {
        ArgumentNullException.ThrowIfNull(above);
        if (!walkComplete)
        {
            return true;
        }

        foreach (var window in above)
        {
            if (Covers(target, window))
            {
                return true;
            }
        }

        return false;
    }
}
