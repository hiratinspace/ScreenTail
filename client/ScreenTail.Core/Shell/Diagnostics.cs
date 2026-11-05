using System.Globalization;
using System.Text;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>
/// What "What's being captured right now?" shows, and what Copy diagnostics puts on the clipboard
/// (ST-071, Spec §5 S1).
///
/// <b>Every field here is a state, a count or a name the technician could have read off the registry.</b>
/// That is not a convention to remember — it is what the type is. The panel exists to answer a customer
/// standing behind a technician asking "what is that recording?", and the clipboard text goes into a
/// ticket or an email, so a window title or a line of OCR reaching it would be the same leak as one in the
/// audit log (INV-10).
///
/// <b>The active window is named by its process, never by its title.</b> "Not capturing: outlook" is the
/// answer to the question; "Acme Dental — unpaid invoices — Outlook" is a customer's business in a
/// support ticket. <see cref="ScreenTail.Core.Detection.ScopeDecision.Reason"/> is already written to that
/// rule, which is why this takes it whole rather than rebuilding it.
/// </summary>
/// <param name="Scope">The scope decision's own words: a process name and what is being done about it.</param>
/// <param name="Microphone">The device name, or null when there is none. A device name, never audio.</param>
/// <param name="Suppression">Why capture is currently suppressed, or null when it is not.</param>
/// <param name="RedactionBacklog">Frames waiting to be read and masked.</param>
/// <param name="FramesDropped">
/// Frames lost unchecked (INV-1): deleted because they could not be read, or refused because the queue of
/// frames waiting to be read was full.
/// </param>
/// <param name="KeystrokesDropped">Keyboard events dropped for being out of scope (INV-6).</param>
/// <param name="EgressBlocked">Requests the egress guard refused (INV-8).</param>
/// <param name="NameHookRefusals">
/// How many times Windows refused to narrow the title-change hook to the window in front, so that the
/// service listened to every window instead (ST-031). Nothing is missed, but the idle service costs more.
/// Null when the service cannot know it, which the panel shows as unknown rather than 0.
/// </param>
public sealed record Diagnostics(
    string Scope,
    string? Microphone,
    string? Suppression,
    int RedactionBacklog,
    long FramesDropped,
    long KeystrokesDropped,
    long EgressBlocked,
    long? NameHookRefusals,
    bool LocalOnly,
    string PolicyVersion,
    double CpuPercent,
    long WorkingSetBytes,
    string ClientVersion)
{
    /// <summary>
    /// What the service answered, field for field. Here rather than in the window so the Mac suite holds
    /// the whole path from the service's count to the panel's row. Every field is a state, a count or a
    /// name; the reply has nothing else to give.
    /// </summary>
    public static Diagnostics From(DiagnosticsReported reported)
    {
        ArgumentNullException.ThrowIfNull(reported);
        return new Diagnostics(
            reported.Scope,
            reported.Microphone,
            reported.Suppression,
            reported.RedactionBacklog,
            reported.FramesDropped,
            reported.KeystrokesDropped,
            reported.EgressBlocked,
            reported.NameHookRefusals,
            reported.LocalOnly,
            reported.PolicyVersion,
            reported.CpuPercent,
            reported.WorkingSetBytes,
            reported.ServiceVersion);
    }

    /// <summary>
    /// The clipboard text. Built from this record alone, so there is no path from a frame, a transcript or
    /// a window title into it — the same argument as <c>AuditExport</c>, and for the same reason.
    /// </summary>
    public string ToClipboardText(DateTimeOffset at)
    {
        var text = new StringBuilder()
            .Append("ScreenTail diagnostics ").Append(at.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)).AppendLine()
            .Append("client           ").AppendLine(ClientVersion)
            .Append("policy           ").AppendLine(PolicyVersion)
            .Append("local-only       ").AppendLine(LocalOnly ? "yes" : "no")
            .Append("scope            ").AppendLine(Scope)
            .Append("microphone       ").AppendLine(Microphone ?? "none")
            .Append("suppressed       ").AppendLine(Suppression ?? "no")
            .Append("redaction queue  ").AppendLine(RedactionBacklog.ToString(CultureInfo.InvariantCulture))
            .Append("frames dropped   ").AppendLine(FramesDropped.ToString(CultureInfo.InvariantCulture))
            .Append("keystrokes held  ").AppendLine(KeystrokesDropped.ToString(CultureInfo.InvariantCulture))
            .Append("egress blocked   ").AppendLine(EgressBlocked.ToString(CultureInfo.InvariantCulture))
            .Append("hook fallbacks   ").AppendLine(NameHookRefusals?.ToString(CultureInfo.InvariantCulture) ?? DiagnosticsPanel.Unknown)
            .Append("cpu              ").AppendLine(CpuPercent.ToString("F1", CultureInfo.InvariantCulture) + "%")
            .Append("memory           ").AppendLine((WorkingSetBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB");

        return text.ToString();
    }

    /// <summary>What the panel says about capture, in one line a customer can read over a shoulder.</summary>
    public string Headline => Suppression is { } why
        ? $"Paused: {why}"
        : Scope;
}
