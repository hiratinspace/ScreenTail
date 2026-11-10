using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.History;
using ScreenTail.Core.Review;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.UI.History;

/// <summary>One row of the History screen, already worded (Spec §5 S4).</summary>
/// <param name="Frames">"7", or "7 (+1 purged)" when frames were deleted unredacted, because that is a fact about coverage the technician should see before publishing.</param>
/// <param name="NotConfirmed">A publish of the session may have reached the ticket and was never confirmed: said beside the status, never in place of it (contract v10).</param>
public sealed record HistoryRow(string Id, string Started, string Duration, string Status, string Tool, string Frames, bool NotConfirmed = false)
{
    /// <summary>The row as a screen reader says it (Spec §7); it read the record itself until ST-084.</summary>
    public string Spoken => NotConfirmed
        ? $"{Status}, publish not confirmed, started {Started}, {Duration}, {Tool}, {Frames} screenshots"
        : $"{Status}, started {Started}, {Duration}, {Tool}, {Frames} screenshots";

    /// <summary>The second badge's words, or nothing for a row that has no such badge.</summary>
    public string NotConfirmedLabel => NotConfirmed ? SessionHistory.NotConfirmedLabel : string.Empty;

    public System.Windows.Visibility NotConfirmedVisibility => NotConfirmed ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}

/// <summary>
/// The History screen (ST-079), fed by <c>list_sessions</c> over the pipe. The service computes the
/// status word so the draft's text never crosses just to be turned back into "Draft"; this only formats.
/// Opening a row is one call on the shell store, which switches the view and names the session at once.
///
/// Multi-select and Bulk discard (Spec §5 S4, v0.4.1 Q6): which selected rows qualify is
/// <see cref="SessionHistory.Discardable(IEnumerable{SessionRow})"/>'s rule, the technician types how
/// many, and the list is read again afterwards so it says what the service now holds.
///
/// "Not confirmed" and Mark as published (contract v10): a row whose publish may have reached the ticket
/// says so beside its status, and with exactly that one row selected the technician can settle it after
/// checking the ticket. The question and the service's rule are <see cref="MarkAsPublished"/>'s and
/// PublishCommands'; this offers the button and shows the answer.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly ShellState _state;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SessionRow>?>> _load;
    private readonly Func<TypedConfirmation, int, Task<bool>>? _confirm;
    private readonly Func<IReadOnlyList<string>, Func<string, Task<bool>>, CancellationToken, Task<ConfirmedResult>>? _discard;
    private readonly Func<IReadOnlyCollection<string>, string?>? _discardBlocked;
    private readonly Func<string, string, CancellationToken, Task<ConfirmedResult>>? _markPublished;
    private readonly Dictionary<string, SessionRow> _sessions = new(StringComparer.Ordinal);
    private IReadOnlyList<HistoryRow> _selected = [];

    /// <param name="load">The service's rows.</param>
    /// <param name="confirm">The typed-confirmation dialog, with how many sessions it is for. Null with no discard wired.</param>
    /// <param name="discard">
    /// Discards the ids once the technician has typed what the service asks for: the second argument shows
    /// the dialog for the service's phrase and says whether it was typed. Null in a screen with nothing
    /// to discard through, which disables Bulk discard.
    /// </param>
    /// <param name="discardBlocked">Why these sessions may not be asked about now (one is still being captured), or null.</param>
    /// <param name="markPublished">
    /// Asks the technician and then the service to mark the session (its id, then the row as History
    /// shows it, for the question: when it started, how long, the tool) as published
    /// (<see cref="MarkAsPublished.RunAsync"/> with the shell's dialog). Null in a screen with nothing to
    /// ask through, which disables the button.
    /// </param>
    public HistoryViewModel(
        ShellState state,
        Func<CancellationToken, Task<IReadOnlyList<SessionRow>?>> load,
        Func<TypedConfirmation, int, Task<bool>>? confirm = null,
        Func<IReadOnlyList<string>, Func<string, Task<bool>>, CancellationToken, Task<ConfirmedResult>>? discard = null,
        Func<IReadOnlyCollection<string>, string?>? discardBlocked = null,
        Func<string, string, CancellationToken, Task<ConfirmedResult>>? markPublished = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _confirm = confirm;
        _discard = discard;
        _discardBlocked = discardBlocked;
        _markPublished = markPublished;
    }

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = "Asking the capture service…";

    /// <summary>What the last discard or mark as published did, for the line beside the buttons. Empty until there is one.</summary>
    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    /// <summary>Why the last discard or mark as published did not happen, or null. Shown beside the buttons, never as a dialog.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscardFailed))]
    public partial string? DiscardError { get; set; }

    public bool DiscardFailed => DiscardError is not null;

    /// <summary>Raised after a discard or a mark as published the service carried out, so the pane can put focus back on the list.</summary>
    public event EventHandler? Discarded;

    /// <summary>The rows the technician has selected, in the order the table shows them.</summary>
    public IReadOnlyList<HistoryRow> Selected => _selected;

    /// <summary>How many of the selected sessions a discard would take: the number to type.</summary>
    public int DiscardableCount => Qualifying().Count;

    /// <summary>
    /// The line under the table about the selection: how many are selected, and how many of those are
    /// kept whatever happens, so the number the dialog asks for is never a surprise.
    /// </summary>
    public string SelectionNote
    {
        get
        {
            var selected = _selected.Count;
            var discardable = DiscardableCount;
            return selected switch
            {
                0 => string.Empty,
                _ when discardable == selected => selected == 1 ? "1 session selected." : string.Create(CultureInfo.CurrentCulture, $"{selected} sessions selected."),
                _ => string.Create(CultureInfo.CurrentCulture, $"{discardable} of {selected} selected can be discarded. Published and already discarded sessions are kept."),
            };
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var rows = await _load(ct).ConfigureAwait(true);
        Rows.Clear();
        _sessions.Clear();
        Select([]);
        if (rows is null)
        {
            Status = "The capture service did not answer.";
            return;
        }

        foreach (var row in rows.OrderByDescending(r => r.StartedAt))
        {
            _sessions[row.Id] = row;
            Rows.Add(Format(row));
        }

        Status = Rows.Count switch
        {
            0 => "No sessions yet. Record one and stop it with Ctrl+Alt+S.",
            1 => "1 session",
            var n => $"{n} sessions",
        };
    }

    /// <summary>The table's selection changed. The pane calls this; the table's selection is not bindable.</summary>
    public void Select(IEnumerable<HistoryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var chosen = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        _selected = [.. Rows.Where(r => chosen.Contains(r.Id))];
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(DiscardableCount));
        OnPropertyChanged(nameof(SelectionNote));
        BulkDiscardCommand.NotifyCanExecuteChanged();
        MarkPublishedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Open(HistoryRow? row)
    {
        if (row is not null)
        {
            _state.OpenSession(row.Id);
        }
    }

    /// <summary>
    /// Spec §5 S4's Bulk discard. Only the selected sessions that qualify are named, and the technician
    /// types how many (v0.4.1 Q6). Nothing is asked while one of them is still being captured (Spec §4).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanBulkDiscard))]
    private async Task BulkDiscardAsync()
    {
        var ids = Qualifying().Select(r => r.Id).ToList();
        if (_discard is null || _confirm is null || ids.Count == 0)
        {
            return;
        }

        if (_discardBlocked?.Invoke(ids) is { } blocked)
        {
            DiscardError = blocked;
            return;
        }

        DiscardError = null;
        var confirm = _confirm;
        var result = await _discard(ids, phrase => confirm(new TypedConfirmation(phrase), ids.Count), CancellationToken.None).ConfigureAwait(true);
        switch (result.Outcome)
        {
            case ConfirmedOutcome.Declined:
                return;
            case ConfirmedOutcome.Done:
                await RefreshAsync().ConfigureAwait(true);
                Notice = ids.Count == 1 ? "1 session discarded." : string.Create(CultureInfo.CurrentCulture, $"{ids.Count} sessions discarded.");
                Discarded?.Invoke(this, EventArgs.Empty);
                return;
            default:
                // Refused or not answered. What the service holds may have moved under the selection, so
                // the list is read again, and the reason stays beside the button.
                DiscardError = result.Error ?? ConfirmedAction.RefusedWithoutReason;
                await RefreshAsync().ConfigureAwait(true);
                return;
        }
    }

    /// <summary>
    /// The technician checked the ticket and the earlier publish is there (contract v10). One session at a
    /// time, since each is a statement about its own ticket: offered with exactly one row selected, and
    /// that row not confirmed. They are asked first; nothing is asked while a session is being captured,
    /// and the reason is said beside the button instead (Spec §4).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMarkPublished))]
    private async Task MarkPublishedAsync()
    {
        if (_markPublished is null || MarkableSelection() is not { } row)
        {
            return;
        }

        DiscardError = null;
        var result = await _markPublished(row.Id, Described(_selected[0]), CancellationToken.None).ConfigureAwait(true);
        switch (result.Outcome)
        {
            case ConfirmedOutcome.Declined:
                return;
            case ConfirmedOutcome.Done:
                await RefreshAsync().ConfigureAwait(true);
                Notice = MarkAsPublished.Done;
                Discarded?.Invoke(this, EventArgs.Empty);
                return;
            case ConfirmedOutcome.NotAvailable:
                DiscardError = result.Error ?? ConfirmedAction.NotConnected;
                return;
            default:
                // Refused. Blocked by a session being captured leaves the list as it is; anything else
                // may mean what the service holds moved under the selection, so the list is read again.
                var error = result.Error ?? ConfirmedAction.RefusedWithoutReason;
                if (!string.Equals(error, MarkAsPublished.WhileCapturing, StringComparison.Ordinal))
                {
                    await RefreshAsync().ConfigureAwait(true);
                }

                DiscardError = error;
                return;
        }
    }

    /// <summary>The row in the words it already shows, so the question names the session without anything new.</summary>
    internal static string Described(HistoryRow row) => $"{row.Started}, {row.Duration}, {row.Tool}";

    private bool CanMarkPublished() => _markPublished is not null && MarkableSelection() is not null;

    /// <summary>The one selected session, when it is one whose publish is not confirmed; otherwise null.</summary>
    private SessionRow? MarkableSelection() =>
        _selected.Count == 1 && _sessions.GetValueOrDefault(_selected[0].Id) is { } row && SessionHistory.MayMarkPublished(row) ? row : null;

    private bool CanBulkDiscard() => _discard is not null && _confirm is not null && DiscardableCount > 0;

    private IReadOnlyList<SessionRow> Qualifying() =>
        SessionHistory.Discardable(_selected.Select(r => _sessions.GetValueOrDefault(r.Id)).OfType<SessionRow>());

    internal static HistoryRow Format(SessionRow row)
    {
        var duration = TimeSpan.FromMilliseconds(row.DurationMs);
        return new HistoryRow(
            row.Id,
            row.StartedAt.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture),
            duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture),
            row.Status.Length == 0 ? row.Status : char.ToUpperInvariant(row.Status[0]) + row.Status[1..],
            string.IsNullOrEmpty(row.Tool) ? "Unknown" : row.Tool,
            row.FramesPurged > 0 ? $"{row.Frames} (+{row.FramesPurged} purged)" : row.Frames.ToString(CultureInfo.InvariantCulture),
            SessionHistory.MayMarkPublished(row));
    }
}
