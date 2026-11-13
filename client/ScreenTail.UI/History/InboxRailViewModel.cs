using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.History;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.UI.History;

/// <summary>One row of the rail. Updated in place when the list is read again, so focus on it stays.</summary>
public sealed partial class InboxRowViewModel : ObservableObject
{
    public InboxRowViewModel(InboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Id = item.Row.Id;
        Update(item);
    }

    public string Id { get; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>The note's title under the company, or empty when the row has no company or no title (spec v0.6.9 S4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleVisibility))]
    public partial string Subtitle { get; set; } = string.Empty;

    /// <summary>The row's words in full, for the tool tip, since a narrow rail ends long ones in an ellipsis.</summary>
    [ObservableProperty]
    public partial string Full { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Age { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Meta { get; set; } = string.Empty;

    /// <summary>The state badge's words, or empty for a row whose group already says it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeVisibility))]
    public partial string Badge { get; set; } = string.Empty;

    /// <summary>Which state colour the badge's glyph and border take: Published, Paused, Offline or Muted.</summary>
    [ObservableProperty]
    public partial string BadgeTone { get; set; } = "Muted";

    /// <summary>The badge's glyph, beside its word.</summary>
    [ObservableProperty]
    public partial string BadgeGlyph { get; set; } = string.Empty;

    /// <summary>The row as a screen reader says it.</summary>
    [ObservableProperty]
    public partial string Spoken { get; set; } = string.Empty;

    /// <summary>The session pane shows this row's session: the row is drawn as the pane's tab.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    public Visibility BadgeVisibility => Badge.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SubtitleVisibility => Subtitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    internal void Update(InboxItem item)
    {
        Title = item.Title;
        Subtitle = item.Subtitle ?? string.Empty;
        Age = item.Age;
        Meta = item.Meta;
        Badge = item.Badge ?? string.Empty;

        // The same word, glyph and hue the All sessions table draws (spec v0.6.9 S4).
        var badge = StatusBadges.Of(item.Row.Status);
        (BadgeTone, BadgeGlyph) = (badge.Tone, badge.Glyph);
        Full = string.Join(Environment.NewLine, new[] { item.Title, item.Subtitle, item.Meta }.Where(w => !string.IsNullOrEmpty(w)));
        Spoken = item.Spoken;
    }
}

/// <summary>One group of the rail: its name, its count and its rows. Hidden while it has none.</summary>
public sealed partial class InboxGroupViewModel : ObservableObject
{
    public InboxGroupViewModel(InboxGroup group)
    {
        Group = group;
        Name = InboxRail.Name(group);
        Collapsible = group == InboxGroup.Earlier;
        IsExpanded = !Collapsible;
    }

    public InboxGroup Group { get; }

    public string Name { get; }

    /// <summary>Earlier only: it starts collapsed (Spec §5 S4).</summary>
    public bool Collapsible { get; }

    public ObservableCollection<InboxRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Visibility), nameof(Heading), nameof(CountText))]
    public partial int Count { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowsVisibility), nameof(Chevron))]
    public partial bool IsExpanded { get; set; }

    public string CountText => Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>"Earlier, 31 sessions": the group's list and its heading as a screen reader hears them.</summary>
    public string Heading => InboxRail.Heading(Group, Count);

    public Visibility Visibility => Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RowsVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The collapsible heading's glyph: chevron right when collapsed, down when open.</summary>
    public string Chevron => IsExpanded ? "" : "";

    public Visibility HeaderTextVisibility => Collapsible ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ToggleVisibility => Collapsible ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// The inbox rail (Spec v0.6.0 §5 S4, as built v0.6.4): History as the workspace's left rail. Reads the
/// same <c>list_sessions</c> rows History does and groups them with <see cref="InboxRail"/>. Opening a row
/// is one call on the shell store, as a History row's is, and the row of the session the pane shows is
/// drawn as its tab.
///
/// Reading the list again updates the rows in place rather than building new ones, because the list is
/// read when a session is opened: rebuilt, the row a keyboard user had just arrowed to would go, and
/// focus with it.
/// </summary>
public sealed partial class InboxRailViewModel : ObservableObject
{
    private readonly ShellState _state;
    private readonly Func<CancellationToken, Task<IReadOnlyList<SessionRow>?>> _load;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private IReadOnlyList<SessionRow>? _rows;
    private string? _open;
    private int _reads;

    public InboxRailViewModel(ShellState state, Func<CancellationToken, Task<IReadOnlyList<SessionRow>?>> load, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
        Groups = [.. Enum.GetValues<InboxGroup>().Select(g => new InboxGroupViewModel(g))];
    }

    public IReadOnlyList<InboxGroupViewModel> Groups { get; }

    /// <summary>What the search box holds. Every change filters the rows at once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchHintVisibility))]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>The line in place of the rows: asking, not answered, no sessions yet, or nothing matches. Empty when there are rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoticeVisibility))]
    public partial string Notice { get; set; } = InboxRail.Asking;

    /// <summary>Clear search, offered beside the no-match line.</summary>
    [ObservableProperty]
    public partial Visibility ClearSearchVisibility { get; set; } = Visibility.Collapsed;

    public Visibility NoticeVisibility => Notice.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SearchHintVisibility => Query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Every row on the rail, in its order, for tests and the harness.</summary>
    public IEnumerable<InboxRowViewModel> Rows => Groups.SelectMany(g => g.Rows);

    /// <summary>Reads the list again. Only the latest read is shown, whichever finishes first.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var read = ++_reads;
        var rows = await _load(ct).ConfigureAwait(true);
        if (read != _reads)
        {
            return;
        }

        _rows = rows;
        Show();
    }

    /// <summary>The session the pane shows, or null when it shows none (another view, or nothing to review).</summary>
    public void Sync(string? openSessionId)
    {
        _open = openSessionId;
        foreach (var group in Groups)
        {
            foreach (var row in group.Rows)
            {
                row.IsOpen = row.Id == openSessionId;
                if (row.IsOpen && !group.IsExpanded)
                {
                    // The tab is never hidden in a collapsed group.
                    group.IsExpanded = true;
                }
            }
        }
    }

    /// <summary>A row clicked, or reached with the arrows: its session in the pane, through the store.</summary>
    public void Open(InboxRowViewModel? row)
    {
        if (row is not null && !row.IsOpen)
        {
            _state.OpenSession(row.Id);
        }
    }

    [RelayCommand]
    private void ClearSearch() => Query = string.Empty;

    partial void OnQueryChanged(string value)
    {
        // Rows matched in Earlier are shown, not left behind its heading.
        if (value.Trim().Length > 0)
        {
            foreach (var group in Groups.Where(g => g.Collapsible))
            {
                group.IsExpanded = true;
            }
        }

        Show();
    }

    private void Show()
    {
        if (_rows is null)
        {
            Fill([]);
            Notice = _reads == 0 ? InboxRail.Asking : InboxRail.NotAnswered;
            ClearSearchVisibility = Visibility.Collapsed;
            return;
        }

        var items = InboxRail.Items(_rows, Query, _time.GetUtcNow(), _zone);
        Fill(items);
        var searching = Query.Trim().Length > 0;
        Notice = items.Count > 0 ? string.Empty
            : searching ? InboxRail.NoMatch(Query)
            : SessionHistory.Empty;
        ClearSearchVisibility = items.Count == 0 && searching ? Visibility.Visible : Visibility.Collapsed;
        Sync(_open);
    }

    private void Fill(IReadOnlyList<InboxItem> items)
    {
        foreach (var group in Groups)
        {
            var wanted = items.Where(i => i.Group == group.Group).ToList();
            Reconcile(group.Rows, wanted);
            group.Count = wanted.Count;
        }
    }

    /// <summary>Makes <paramref name="rows"/> the <paramref name="wanted"/> items, keeping each row that stays.</summary>
    private static void Reconcile(ObservableCollection<InboxRowViewModel> rows, List<InboxItem> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var id = wanted[i].Row.Id;
            if (i < rows.Count && rows[i].Id == id)
            {
                rows[i].Update(wanted[i]);
                continue;
            }

            var at = -1;
            for (var j = i + 1; j < rows.Count; j++)
            {
                if (rows[j].Id == id)
                {
                    at = j;
                    break;
                }
            }

            if (at >= 0)
            {
                rows.Move(at, i);
                rows[i].Update(wanted[i]);
            }
            else
            {
                rows.Insert(i, new InboxRowViewModel(wanted[i]));
            }
        }

        while (rows.Count > wanted.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }
}
