using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoreShell = ScreenTail.Core.Shell;

namespace ScreenTail.UI.Diagnostics;

/// <param name="Label">The left column. Fixed wording, never derived from anything captured.</param>
public sealed record DiagnosticsRow(string Label, string Value);

/// <summary>
/// The "What's being captured right now?" panel (ST-071, Spec §5 S1).
///
/// Every row is built from <see cref="CoreShell.Diagnostics"/>, which has nowhere to put content — so the
/// panel cannot show a window title or a line of OCR even by mistake, and neither can the clipboard text
/// the Copy button produces (INV-10).
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly CoreShell.Diagnostics _diagnostics;
    private readonly Action<string> _copy;
    private readonly TimeProvider _time;

    public DiagnosticsViewModel(CoreShell.Diagnostics diagnostics, Action<string>? copy = null, TimeProvider? time = null)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _copy = copy ?? System.Windows.Clipboard.SetText;
        _time = time ?? TimeProvider.System;

        Headline = _diagnostics.Headline;
        Rows =
        [
            new DiagnosticsRow("Scope", _diagnostics.Scope),
            new DiagnosticsRow("Microphone", _diagnostics.Microphone ?? "none"),
            new DiagnosticsRow("Suppressed", _diagnostics.Suppression ?? "no"),
            new DiagnosticsRow("Redaction queue", _diagnostics.RedactionBacklog.ToString(CultureInfo.InvariantCulture)),
            new DiagnosticsRow("Frames dropped", _diagnostics.FramesDropped.ToString(CultureInfo.InvariantCulture)),
            new DiagnosticsRow("Keystrokes held", _diagnostics.KeystrokesDropped.ToString(CultureInfo.InvariantCulture)),
            new DiagnosticsRow("Egress blocked", _diagnostics.EgressBlocked.ToString(CultureInfo.InvariantCulture)),
            new DiagnosticsRow("Local-only", _diagnostics.LocalOnly ? "yes" : "no"),
            new DiagnosticsRow("Policy", _diagnostics.PolicyVersion),
            new DiagnosticsRow("CPU", _diagnostics.CpuPercent.ToString("F1", CultureInfo.InvariantCulture) + "%"),
            new DiagnosticsRow("Memory", (_diagnostics.WorkingSetBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MB"),
            new DiagnosticsRow("Client", _diagnostics.ClientVersion),
        ];
    }

    public string Headline { get; }

    public IReadOnlyList<DiagnosticsRow> Rows { get; }

    [RelayCommand]
    private void Copy() => _copy(_diagnostics.ToClipboardText(_time.GetUtcNow()));
}
