using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ScreenTail.Core.Lifecycle;

namespace ScreenTail.Platform.Lifecycle;

/// <summary>
/// Writes a start refusal to the Windows Application log, where somebody can find it (ST-112): an installed
/// build is a Windows program, and its standard error goes nowhere.
///
/// Through <c>RegisterEventSource</c> and <c>ReportEvent</c>, which need no administrator rights and no
/// registered source. Event Viewer shows the entry under the source "ScreenTail" with the fixed sentence as
/// its text, and notes that the source has no message file. .NET's <c>EventLog.WriteEntry</c> is not used:
/// it tries to create the source, which needs administrator rights. The text is the refusal's fixed sentence
/// and nothing else: no user, no SID, no path (INV-10).
/// </summary>
[SupportedOSPlatform("windows")]
public static class RefusalEventLog
{
    /// <summary>The source name the entries carry.</summary>
    public const string Source = "ScreenTail";

    /// <summary>Writes the entry. Returns whether Windows took it; a refusal goes on to exit either way.</summary>
    public static bool Write(StartRefusal refusal, bool ui)
    {
        var text = ui ? StartGuard.ExplainForUi(refusal) : StartGuard.Explain(refusal);
        if (text.Length == 0)
        {
            return false;
        }

        var source = RegisterEventSource(null, Source);
        if (source == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return ReportEvent(source, EVENTLOG_WARNING_TYPE, 0, (uint)StartGuard.EventId(refusal, ui), IntPtr.Zero, 1, 0, [text], IntPtr.Zero);
        }
        finally
        {
            _ = DeregisterEventSource(source);
        }
    }

    private const ushort EVENTLOG_WARNING_TYPE = 0x0002;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterEventSource(string? server, string source);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeregisterEventSource(IntPtr source);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReportEvent(IntPtr source, ushort type, ushort category, uint eventId, IntPtr userSid, ushort strings, uint dataSize, string[] texts, IntPtr data);
}
