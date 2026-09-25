using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenTail.Platform.Clipboard;

/// <summary>
/// One read of the clipboard's text, for the ticket hint at session start (ST-077).
///
/// The Win32 clipboard directly rather than WPF's: the service has no WPF, and the raw API has no thread
/// requirement. Read once, when a session starts, and handed to <c>TicketHint</c> which keeps digits or
/// nothing; the text itself is not kept, logged or passed on — a clipboard is whatever the technician
/// last copied, which may be a password (INV-10, ST-077 AC3). Anything that is not plain text, or a
/// clipboard held open by another process, is null.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ClipboardText
{
    private const uint CF_UNICODETEXT = 13;
    private const int LongestRead = 4_096;

    public static string? ReadOnce()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT) || !OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var text = Marshal.PtrToStringUni(pointer);
                return text is { Length: > LongestRead } ? text[..LongestRead] : text;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);
}
