using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.UI.History;

/// <summary>One row of the History screen, already worded (Spec §5 S4).</summary>
/// <param name="Frames">"7", or "7 (+1 purged)" when frames were deleted unredacted, because that is a fact about coverage the technician should see before publishing.</param>
public sealed record HistoryRow(string Id, string Started, string Duration, string Status, string Tool, string Frames);

/// <summary>
/// The History screen (ST-079), fed by <c>list_sessions</c> over the pipe. The service computes the
/// status word so the draft's text never crosses just to be turned back into "Draft"; this only formats.
/// Opening a row is one call on the shell store, which switches the view and names the session at once.
/// </summary>
public sealed partial class HistoryViewModel(
    ShellState state,
    Func<CancellationToken, Task<IReadOnlyList<SessionRow>?>> load) : ObservableObject
{
    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = "Asking the capture service…";

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var rows = await load(ct).ConfigureAwait(true);
        Rows.Clear();
        if (rows is null)
        {
            Status = "The capture service did not answer.";
            return;
        }

        foreach (var row in rows.OrderByDescending(r => r.StartedAt))
        {
            Rows.Add(Format(row));
        }

        Status = Rows.Count switch
        {
            0 => "No sessions yet. Record one and stop it with Ctrl+Alt+S.",
            1 => "1 session",
            var n => $"{n} sessions",
        };
    }

    [RelayCommand]
    private void Open(HistoryRow? row)
    {
        if (row is not null)
        {
            state.OpenSession(row.Id);
        }
    }

    internal static HistoryRow Format(SessionRow row)
    {
        var duration = TimeSpan.FromMilliseconds(row.DurationMs);
        return new HistoryRow(
            row.Id,
            row.StartedAt.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture),
            duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture),
            row.Status.Length == 0 ? row.Status : char.ToUpperInvariant(row.Status[0]) + row.Status[1..],
            string.IsNullOrEmpty(row.Tool) ? "—" : row.Tool,
            row.FramesPurged > 0 ? $"{row.Frames} (+{row.FramesPurged} purged)" : row.Frames.ToString(CultureInfo.InvariantCulture));
    }
}
