using System.Globalization;
using System.Text;

namespace ScreenTail.Core.Shell;

/// <param name="Label">The left column. Fixed wording, never derived from anything captured.</param>
public sealed record DiagnosticsLine(string Label, string Value);

/// <summary>
/// What the "What's being captured right now?" panel says, row by row (ST-071, Spec §5 S1), including
/// when the service cannot be asked.
///
/// The panel is the thing a technician turns round and shows a customer. When the service is not
/// answering it used to show a <see cref="Diagnostics"/> built from zeros, so the customer read "0 frames
/// dropped" and "Local-only: no", neither of which anybody knew (2026-10-12 review, T-152). "We cannot
/// ask" and "none" are different claims, and only the first is true then. So an unanswered panel says
/// unknown on every row, here, where it can be tested without a window (ADR-0002).
///
/// Every row is built from <see cref="Diagnostics"/>, which has nowhere to put content, so the panel
/// cannot show a window title or a line of OCR even by mistake, and neither can the clipboard text (INV-10).
/// </summary>
public sealed class DiagnosticsPanel
{
    public const string Unknown = "unknown";

    public const string NotConnected = "Not connected to the capture service. Capture state unknown.";

    private readonly Diagnostics? _diagnostics;

    private DiagnosticsPanel(Diagnostics? diagnostics)
    {
        _diagnostics = diagnostics;
        Rows = diagnostics is null ? UnansweredRows() : AnsweredRows(diagnostics);
    }

    /// <summary>The panel when the service is not answering. Every count is unknown rather than zero.</summary>
    public static DiagnosticsPanel Unanswered { get; } = new(null);

    public static DiagnosticsPanel From(Diagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new DiagnosticsPanel(diagnostics);
    }

    /// <summary>Whether the service answered. False means every row below says unknown.</summary>
    public bool Answered => _diagnostics is not null;

    /// <summary>What the panel says about capture, in one line a customer can read over a shoulder.</summary>
    public string Headline => _diagnostics?.Headline ?? NotConnected;

    public IReadOnlyList<DiagnosticsLine> Rows { get; }

    /// <summary>
    /// The clipboard text. The service's own when it answered; when it did not, a note saying so and
    /// nothing that reads as a measurement.
    /// </summary>
    public string ToClipboardText(DateTimeOffset at)
    {
        if (_diagnostics is { } diagnostics)
        {
            return diagnostics.ToClipboardText(at);
        }

        var text = new StringBuilder()
            .Append("ScreenTail diagnostics ").Append(at.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture)).AppendLine()
            .AppendLine(NotConnected);
        foreach (var row in Rows)
        {
            text.Append(row.Label.ToLowerInvariant().PadRight(17)).AppendLine(row.Value);
        }

        return text.ToString();
    }

    private static IReadOnlyList<DiagnosticsLine> AnsweredRows(Diagnostics d) =>
    [
        new("Scope", d.Scope),
        new("Microphone", d.Microphone ?? "none"),
        new("Suppressed", d.Suppression ?? "no"),
        new("Redaction queue", d.RedactionBacklog.ToString(CultureInfo.InvariantCulture)),
        new("Frames dropped", d.FramesDropped.ToString(CultureInfo.InvariantCulture)),
        new("Keystrokes held", d.KeystrokesDropped.ToString(CultureInfo.InvariantCulture)),
        new("Egress blocked", d.EgressBlocked.ToString(CultureInfo.InvariantCulture)),
        new("Local-only", d.LocalOnly ? "yes" : "no"),
        new("Policy", d.PolicyVersion),
        new("CPU", d.CpuPercent.ToString("F1", CultureInfo.InvariantCulture) + "%"),
        new("Memory", (d.WorkingSetBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB"),
        new("Client", d.ClientVersion),
    ];

    private static IReadOnlyList<DiagnosticsLine> UnansweredRows() =>
    [
        new("Scope", "Not connected to the capture service"),
        new("Microphone", Unknown),
        new("Suppressed", Unknown),
        new("Redaction queue", Unknown),
        new("Frames dropped", Unknown),
        new("Keystrokes held", Unknown),
        new("Egress blocked", Unknown),
        new("Local-only", Unknown),
        new("Policy", Unknown),
        new("CPU", Unknown),
        new("Memory", Unknown),
        new("Client", Unknown),
    ];
}
