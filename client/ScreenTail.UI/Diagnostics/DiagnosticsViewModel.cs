using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoreShell = ScreenTail.Core.Shell;

namespace ScreenTail.UI.Diagnostics;

/// <param name="Label">The left column. Fixed wording, never derived from anything captured.</param>
public sealed record DiagnosticsRow(string Label, string Value);

/// <summary>
/// The "What's being captured right now?" panel (ST-071, Spec §5 S1).
///
/// Every row comes from <see cref="CoreShell.DiagnosticsPanel"/> over <see cref="CoreShell.Diagnostics"/>,
/// which has nowhere to put content — so the panel cannot show a window title or a line of OCR even by
/// mistake, and neither can the clipboard text the Copy button produces (INV-10). The panel also decides
/// what to say when the service cannot be asked, so that is tested without a window.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly CoreShell.DiagnosticsPanel _panel;
    private readonly Action<string> _copy;
    private readonly TimeProvider _time;

    public DiagnosticsViewModel(CoreShell.Diagnostics diagnostics, Action<string>? copy = null, TimeProvider? time = null)
        : this(CoreShell.DiagnosticsPanel.From(diagnostics ?? throw new ArgumentNullException(nameof(diagnostics))), copy, time)
    {
    }

    /// <param name="panel">The rows, answered by the service or saying unknown on every one when it could not be asked (T-152).</param>
    public DiagnosticsViewModel(CoreShell.DiagnosticsPanel panel, Action<string>? copy = null, TimeProvider? time = null)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _copy = copy ?? System.Windows.Clipboard.SetText;
        _time = time ?? TimeProvider.System;

        Headline = _panel.Headline;
        Rows = [.. _panel.Rows.Select(line => new DiagnosticsRow(line.Label, line.Value))];
    }

    public string Headline { get; }

    public IReadOnlyList<DiagnosticsRow> Rows { get; }

    [RelayCommand]
    private void Copy() => _copy(_panel.ToClipboardText(_time.GetUtcNow()));
}
