namespace ScreenTail.Core.Detection;

/// <summary>
/// The window in front, as much of it as Windows will tell us (ST-022). Everything here is a window
/// property — title, process, class — and never window content: nothing read here is captured (INV-2).
/// </summary>
/// <param name="ProcessId">0 when the window vanished before we could resolve it.</param>
/// <param name="ProcessName">Executable name without extension, or null when the process is out of reach.</param>
/// <param name="Title">Window title. Empty is normal — many windows have none.</param>
/// <param name="BrowserTabTitle">
/// For a browser, the active tab's title with the browser's own suffix removed. Null for everything else.
/// </param>
/// <param name="IsElevated">
/// True when the window belongs to a process this one can't open. Capture is blind to those windows, so
/// they must be treated as "we can't see it" rather than "nothing happened" (Spec §5 S2).
/// </param>
public sealed record ForegroundWindowInfo(
    nint Handle,
    int ProcessId,
    string? ProcessName,
    string Title,
    string ClassName,
    string? BrowserTabTitle,
    bool IsElevated,
    DateTimeOffset At)
{
    /// <summary>Nothing is in the foreground — the desktop, a lock screen, or a window that just closed.</summary>
    public static ForegroundWindowInfo None(DateTimeOffset at) =>
        new(0, 0, null, string.Empty, string.Empty, null, false, at);

    public bool IsNone => Handle == 0;

    /// <summary>Whether this is the same window showing the same thing. A retitled tab is a change; a repeat isn't.</summary>
    public bool SameAs(ForegroundWindowInfo? other) =>
        other is not null
        && other.Handle == Handle
        && other.ProcessId == ProcessId
        && string.Equals(other.Title, Title, StringComparison.Ordinal);

    /// <summary>What may be logged: identity and shape, never the title (INV-10).</summary>
    public string ForLog() => IsNone
        ? "none"
        : $"{ProcessName ?? "unknown"}#{ProcessId} class={ClassName} title={Title.Length} chars{(IsElevated ? " elevated" : string.Empty)}";
}
