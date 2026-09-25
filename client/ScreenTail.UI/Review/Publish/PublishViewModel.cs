using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Shared.Schema;

namespace ScreenTail.UI.Review.Publish;

/// <summary>One line of the result list: what landed, where, or why not.</summary>
public sealed record ResultRow(string Label, bool Ok, Uri? Link, string? Error)
{
    public string Mark => Ok ? "✓" : "✗";

    public Visibility LinkVisibility => Link is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ErrorVisibility => Error is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// The right pane (ST-078, Spec §5 S3). Binding over <see cref="PublishPanel"/>, which holds every rule
/// and is tested without WPF; this only turns its properties into things a control can bind to and its
/// two actions into commands.
///
/// Everything the view binds to is a string, a bool or a visibility computed here, so the wording is
/// the spec's and lives where it can be read without opening XAML.
/// </summary>
public sealed partial class PublishViewModel : ObservableObject
{
    private readonly PublishPanel _panel;
    private readonly Func<DraftNote> _note;
    private readonly Func<IReadOnlyList<string>> _frames;
    private bool _choicesAsked;

    /// <param name="note">The note as it is now, edited; asked for at the moment of publishing.</param>
    /// <param name="frames">The included frames, in strip order, at the moment of publishing.</param>
    public PublishViewModel(PublishPanel panel, Func<DraftNote> note, Func<IReadOnlyList<string>> frames)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _note = note ?? throw new ArgumentNullException(nameof(note));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        Refresh();
    }

    public ObservableCollection<TicketMatch> Matches { get; } = [];

    public ObservableCollection<ResultRow> Results { get; } = [];

    /// <summary>The documentation platform's companies, for the mapping prompt (ST-097). Filled when the prompt shows.</summary>
    public ObservableCollection<CompanyChoice> Companies { get; } = [];

    [ObservableProperty]
    public partial CompanyChoice? SelectedCompany { get; set; }

    [ObservableProperty]
    public partial string MappingPrompt { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MappingError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanMap { get; set; }

    [ObservableProperty]
    public partial Visibility MappingVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility MappingErrorVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial TicketMatch? SelectedMatch { get; set; }

    /// <summary>"Recent tickets" over the list on focus, "Matches" once something is typed (Spec §5 S3).</summary>
    [ObservableProperty]
    public partial string MatchesHeading { get; set; } = "Recent tickets";

    [ObservableProperty]
    public partial string TicketLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Minutes { get; set; } = "0";

    [ObservableProperty]
    public partial string RoundingNote { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsInternal { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDiscussion { get; set; }

    [ObservableProperty]
    public partial bool TicketNote { get; set; } = true;

    [ObservableProperty]
    public partial bool TimeEntry { get; set; } = true;

    [ObservableProperty]
    public partial bool KbArticle { get; set; }

    [ObservableProperty]
    public partial string KbLabel { get; set; } = "KB article";

    [ObservableProperty]
    public partial string BlockedBecause { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanPublish { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility FormVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility ResultsVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility MatchesVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility TicketVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility SuggestedVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility BlockedVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility PublishVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial Visibility ConnectVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility PublishedVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility RetryVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>`Ctrl+Enter`, and the button. Refused by the panel when blocked, whatever pressed it.</summary>
    [RelayCommand]
    private async Task PublishAsync()
    {
        if (!_panel.CanPublish)
        {
            return;
        }

        try
        {
            await _panel.PublishAsync(_note(), _frames()).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The provider's words reach the technician through the result list; a throw that the
            // delegate did not turn into a result is one the list cannot show, so it is said here.
            Summary = $"Publishing failed: {error.Message}";
        }

        Refresh();
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        await _panel.RetryAsync(_note(), _frames()).ConfigureAwait(true);
        Refresh();
    }

    /// <summary>
    /// The prompt's answer (ST-097 AC2): the chosen company is remembered by the backend and the article
    /// is sent again, on its own. A refusal leaves the prompt up with a line saying so, because the
    /// article is still not published and the technician should not have to guess whether it is.
    /// </summary>
    [RelayCommand]
    private async Task MapAndPublishAsync()
    {
        if (SelectedCompany is not { } choice || !_panel.NeedsMapping)
        {
            return;
        }

        CanMap = false;
        MappingError = string.Empty;
        MappingErrorVisibility = Visibility.Collapsed;
        var mapped = await _panel.MapAndRetryAsync(choice.Id, _note(), _frames()).ConfigureAwait(true);
        if (!mapped)
        {
            MappingError = "The backend did not take the mapping. Try again, or map it in Settings → Integrations.";
            MappingErrorVisibility = Visibility.Visible;
        }

        Refresh();
    }

    /// <summary>"Open in ConnectWise". Only ever a link the provider handed back, and only https.</summary>
    [RelayCommand]
    private static void OpenLink(Uri? link)
    {
        if (link is { Scheme: "https" })
        {
            _ = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        }
    }

    /// <summary>The picker took focus with nothing typed: show the recent tickets (Spec §5 S3).</summary>
    [RelayCommand]
    private async Task ShowRecentAsync()
    {
        if (Query.Length > 0)
        {
            return;
        }

        var asked = await _panel.RecentAsync().ConfigureAwait(true);
        MatchesHeading = "Recent tickets";
        Show(asked ? _panel.Matches : []);
    }

    partial void OnQueryChanged(string value) => _ = SearchAsync(value);

    partial void OnSelectedCompanyChanged(CompanyChoice? value) => CanMap = value is not null && _panel.NeedsMapping && !_panel.IsPublishing;

    partial void OnSelectedMatchChanged(TicketMatch? value)
    {
        if (value is not null)
        {
            _panel.Choose(value);
            Refresh();
        }
    }

    partial void OnIsInternalChanged(bool value)
    {
        if (value)
        {
            _panel.NoteType = NoteType.Internal;
        }
    }

    partial void OnIsDiscussionChanged(bool value)
    {
        if (value)
        {
            _panel.NoteType = NoteType.Discussion;
        }
    }

    partial void OnMinutesChanged(string value)
    {
        if (int.TryParse(value, out var minutes))
        {
            _panel.Minutes = minutes;
        }
    }

    partial void OnTicketNoteChanged(bool value)
    {
        _panel.TicketNote = value;
        Refresh();
    }

    partial void OnTimeEntryChanged(bool value)
    {
        _panel.TimeEntry = value;
        Refresh();
    }

    partial void OnKbArticleChanged(bool value)
    {
        _panel.KbArticle = value;
        Refresh();
    }

    private async Task SearchAsync(string query)
    {
        var asked = await _panel.SearchAsync(query).ConfigureAwait(true);
        MatchesHeading = "Matches";
        Show(asked ? _panel.Matches : []);
    }

    private void Show(IReadOnlyList<TicketMatch> matches)
    {
        Matches.Clear();
        foreach (var match in matches)
        {
            Matches.Add(match);
        }

        MatchesVisibility = Matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh()
    {
        TicketLabel = _panel.Ticket?.Label ?? string.Empty;
        TicketVisibility = _panel.Ticket is null ? Visibility.Collapsed : Visibility.Visible;
        SuggestedVisibility = _panel.Ticket?.Suggested == true ? Visibility.Visible : Visibility.Collapsed;
        Minutes = _panel.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RoundingNote = _panel.RoundingNote;
        IsInternal = _panel.NoteType == NoteType.Internal;
        IsDiscussion = _panel.NoteType == NoteType.Discussion;
        TicketNote = _panel.TicketNote;
        TimeEntry = _panel.TimeEntry;
        KbArticle = _panel.KbArticle;
        KbLabel = _panel.KbReason is { } reason ? $"KB article  ({reason})" : "KB article";
        BlockedBecause = _panel.HasIntegrations ? _panel.BlockedBecause ?? string.Empty : string.Empty;
        BlockedVisibility = BlockedBecause.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanPublish = _panel.CanPublish;
        PublishVisibility = _panel.HasIntegrations ? Visibility.Visible : Visibility.Collapsed;
        ConnectVisibility = _panel.HasIntegrations ? Visibility.Collapsed : Visibility.Visible;

        var showResults = _panel.Results.Count > 0;
        FormVisibility = showResults ? Visibility.Collapsed : Visibility.Visible;
        ResultsVisibility = showResults ? Visibility.Visible : Visibility.Collapsed;
        PublishedVisibility = _panel.Published ? Visibility.Visible : Visibility.Collapsed;

        // While the article waits on a mapping, the prompt is the retry: Retry alone would fail the same way.
        var needsMapping = _panel.NeedsMapping;
        RetryVisibility = _panel.PartiallyPublished && !needsMapping ? Visibility.Visible : Visibility.Collapsed;
        MappingVisibility = needsMapping ? Visibility.Visible : Visibility.Collapsed;
        MappingPrompt = needsMapping
            ? $"\u201c{_panel.CompanyToMap}\u201d is not mapped to a company in the documentation platform yet. Choose the one it is and the article goes there. The choice is remembered."
            : string.Empty;
        if (needsMapping && !_choicesAsked)
        {
            _choicesAsked = true;
            _ = LoadChoicesAsync();
        }
        else if (!needsMapping && _choicesAsked)
        {
            _choicesAsked = false;
            Companies.Clear();
            SelectedCompany = null;
            MappingError = string.Empty;
            MappingErrorVisibility = Visibility.Collapsed;
        }

        CanMap = needsMapping && SelectedCompany is not null && !_panel.IsPublishing;
        Summary = _panel.Summary ?? Summary;
        Results.Clear();
        foreach (var result in _panel.Results)
        {
            Results.Add(new ResultRow(Label(result.Destination), result.Ok, result.Link, result.Error));
        }
    }

    private async Task LoadChoicesAsync()
    {
        var choices = await _panel.CompanyChoicesAsync().ConfigureAwait(true);
        Companies.Clear();
        foreach (var choice in choices.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Companies.Add(choice);
        }

        if (Companies.Count == 0)
        {
            MappingError = "The documentation platform listed no companies to choose from.";
            MappingErrorVisibility = Visibility.Visible;
        }
    }

    private static string Label(Destination destination) => destination switch
    {
        Destination.TicketNote => "Ticket note",
        Destination.TimeEntry => "Time entry",
        Destination.KbArticle => "KB article",
        _ => destination.ToString(),
    };
}
