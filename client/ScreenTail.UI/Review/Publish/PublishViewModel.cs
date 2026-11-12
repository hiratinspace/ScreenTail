using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Schema;

namespace ScreenTail.UI.Review.Publish;

/// <summary>
/// One line of the result list: what landed, where, or why not, worded by <see cref="PublishResultRow"/>.
/// <see cref="State"/> picks the glyph's colour and the badge's look in the view.
/// </summary>
public sealed record ResultRow(PublishResultRow Row, string? Detail = null)
{
    public string Label => Row.Label;

    /// <summary>What went there, beside the name on the receipt: "Internal note", "0:15", the article's title.</summary>
    public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;

    public ResultState State => Row.State;

    public string Mark => Row.Mark;

    /// <summary>The mark in words for a screen reader, which would otherwise say "question mark".</summary>
    public string MarkName => Row.MarkName;

    /// <summary>The state in a word beside the glyph: Sent, Failed, Not confirmed.</summary>
    public string Badge => Row.Badge;

    public Uri? Link => Row.Link;

    public string? Error => Row.Error;

    /// <summary>"Open in ConnectWise", "Open in Hudu": named for the provider the link goes to.</summary>
    public string LinkText => Row.LinkText;

    public string LinkName => Row.LinkName;

    public Visibility LinkVisibility => Link is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ErrorVisibility => Error is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Where the receipt's Next to check comes from and goes to: History's rows, read when the receipt
/// appears, and the shell's way of opening a session (the same one a History row uses).
/// </summary>
public sealed class NextSessions(Func<CancellationToken, Task<IReadOnlyList<Shared.Ipc.SessionRow>?>> list, Action<string> open)
{
    public Func<CancellationToken, Task<IReadOnlyList<Shared.Ipc.SessionRow>?>> List { get; } = list ?? throw new ArgumentNullException(nameof(list));

    public Action<string> Open { get; } = open ?? throw new ArgumentNullException(nameof(open));
}

/// <summary>
/// The envelope and the receipt (ST-078, Spec v0.6.0 §5 S3, Outbox). Binding over <see cref="PublishPanel"/>,
/// which holds every rule and is tested without WPF; this only turns its properties into things a control
/// can bind to and its actions into commands. Before publishing, the envelope header over the note binds
/// here; after, the receipt that takes the session pane's place.
///
/// Everything the view binds to is a string, a bool or a visibility computed here, so the wording is
/// the spec's and lives where it can be read without opening XAML.
/// </summary>
public sealed partial class PublishViewModel : ObservableObject
{
    private readonly PublishPanel _panel;
    private readonly Func<DraftNote> _note;
    private readonly Func<IReadOnlyList<string>> _frames;

    /// <summary>
    /// Publish, Retry or Map and publish is on its way: one send at a time (<see cref="OneAtATime"/>). A
    /// second Retry or Map and publish under the first reached the panel's "already in flight" throw,
    /// which nothing caught.
    /// </summary>
    private readonly OneAtATime _sending = new();
    private bool _choicesAsked;

    /// <summary>False while the Minutes field holds something that is not a whole number of minutes (T-386).</summary>
    private bool _minutesValid = true;

    /// <param name="note">The note as it is now, edited; asked for at the moment of publishing.</param>
    /// <param name="frames">The included frames, in strip order, at the moment of publishing.</param>
    /// <param name="heldBack">
    /// Why Publish must wait for something elsewhere in Review, or null: a blur still being applied, whose
    /// frame would otherwise go out as it was before (review of #257). Asked whenever the block is worked
    /// out; <see cref="FramesChanged"/> is called when it changes.
    /// </param>
    /// <param name="next">History's rows and the way to open one, for the receipt's Next to check. Null shows no next session.</param>
    /// <param name="openSettings">Opens Settings, for "Connect a PSA to publish". Null leaves the words without the way there.</param>
    /// <param name="sessionId">The session this pane publishes, which is never its own next.</param>
    public PublishViewModel(
        PublishPanel panel,
        Func<DraftNote> note,
        Func<IReadOnlyList<string>> frames,
        Func<string?>? heldBack = null,
        NextSessions? next = null,
        Action? openSettings = null,
        string? sessionId = null)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _note = note ?? throw new ArgumentNullException(nameof(note));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _heldBack = heldBack ?? (() => null);
        _next = next;
        _openSettings = openSettings;
        _sessionId = sessionId ?? string.Empty;
        NoteChanged();
        Refresh();
    }

    private readonly Func<string?> _heldBack;
    private readonly NextSessions? _next;
    private readonly Action? _openSettings;
    private readonly string _sessionId;
    private string? _nextId;
    private bool _nextAsked;

    // ---- The envelope ----

    /// <summary>To: the bound ticket as a value, or "No ticket yet".</summary>
    [ObservableProperty]
    public partial string ToValue { get; set; } = PublishPanel.NoTicketYet;

    /// <summary>The ticket as a chip when one is bound; the plain "No ticket yet" when not.</summary>
    [ObservableProperty]
    public partial Visibility TicketChipVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial Visibility NoTicketVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    public partial string TicketId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TicketRest { get; set; } = string.Empty;

    /// <summary>"Matched from the ScreenConnect window title", or empty for a ticket the technician chose.</summary>
    [ObservableProperty]
    public partial string MatchedFrom { get; set; } = string.Empty;

    /// <summary>Change, or Choose a ticket with none bound.</summary>
    [ObservableProperty]
    public partial string ChangeLabel { get; set; } = "Choose a ticket";

    /// <summary>Change is there only when a PSA is connected: with none, there is nothing to search.</summary>
    [ObservableProperty]
    public partial Visibility ChangeVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>The ticket picker, open in place under To (Ctrl+T or Change), closed otherwise.</summary>
    [ObservableProperty]
    public partial Visibility PickerVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>Time: "12 min, rounded up to 15".</summary>
    [ObservableProperty]
    public partial string TimeNote { get; set; } = string.Empty;

    /// <summary>Why the article is ticked or not, under the Also line (the draft's <c>kb_reason</c>).</summary>
    [ObservableProperty]
    public partial string KbReason { get; set; } = string.Empty;

    /// <summary>A publish that threw before any destination answered, said in the envelope. Empty otherwise.</summary>
    [ObservableProperty]
    public partial string PublishError { get; set; } = string.Empty;

    /// <summary>Beside Publish: "1 inferred step not checked", or empty.</summary>
    [ObservableProperty]
    public partial string OpenItems { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility OpenItemsVisibility { get; set; } = Visibility.Collapsed;

    // ---- The receipt ----

    /// <summary>"Published to ticket #48213", or the state in words when it is not that.</summary>
    [ObservableProperty]
    public partial string ReceiptHeading { get; set; } = string.Empty;

    /// <summary>"Acme Dental · Printer offline · 14:31".</summary>
    [ObservableProperty]
    public partial string ReceiptSubline { get; set; } = string.Empty;

    /// <summary>The heading's state, for its glyph's colour: Sent, Partial, NotConfirmed, Failed.</summary>
    [ObservableProperty]
    public partial ReceiptState ReceiptTone { get; set; } = ReceiptState.Sent;

    /// <summary>The heading's glyph (Segoe Fluent Icons): a check, a warning, a question, a cross. Beside its words.</summary>
    [ObservableProperty]
    public partial string ReceiptGlyph { get; set; } = "";

    public ObservableCollection<ReceiptFact> ReceiptFacts { get; } = [];

    /// <summary>"Need to change something? Edit the note in ConnectWise. ..." once the note is on the ticket.</summary>
    [ObservableProperty]
    public partial string ChangeNote { get; set; } = string.Empty;

    /// <summary>The summary line on the receipt, when it says more than the heading does.</summary>
    [ObservableProperty]
    public partial Visibility SummaryVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>Next to check: the next session's first line, "Remote Desktop session".</summary>
    [ObservableProperty]
    public partial string NextTitle { get; set; } = string.Empty;

    /// <summary>"9 min · started 13:00".</summary>
    [ObservableProperty]
    public partial string NextMeta { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility NextVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>With nothing left, the card says so in place of Open next.</summary>
    [ObservableProperty]
    public partial Visibility NoNextVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string TodayPublished { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TodayLeft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility TodayVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>Settings is reachable from "Connect a PSA to publish".</summary>
    public Visibility OpenSettingsVisibility => _openSettings is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>`Ctrl+T` and Change: the ticket picker opens in place under To. With no PSA there is nothing to pick from.</summary>
    [RelayCommand]
    private void ChangeTicket()
    {
        if (_panel.HasIntegrations && _panel.Results.Count == 0)
        {
            PickerVisibility = Visibility.Visible;
        }
    }

    /// <summary>`Esc` in the picker, Enter on a match, or Done: back to the value.</summary>
    [RelayCommand]
    private void CloseTicketPicker() => PickerVisibility = Visibility.Collapsed;

    /// <summary>`Enter` on the receipt and Open next: the next session in the inbox.</summary>
    [RelayCommand]
    private void OpenNext()
    {
        if (_next is not null && _nextId is { } id && _panel.Results.Count > 0)
        {
            _next.Open(id);
        }
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings?.Invoke();

    /// <summary>The note changed in a way that may change what is still open beside Publish.</summary>
    public void NoteChanged()
    {
        OpenItems = PublishPanel.OpenItems(_note()) ?? string.Empty;
        OpenItemsVisibility = OpenItems.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The backend's PSA is its Development demo (ST-090): said above the form, never a real product's name.</summary>
    public string DemoNotice => _panel.DemoNotice ?? string.Empty;

    public Visibility DemoVisibility => _panel.DemoNotice is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The line under the note type: ConnectWise's wording, or a real PSA's during the demo.</summary>
    public string DiscussionHint => _panel.DiscussionHint;

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

    /// <summary>"Ticket note", or with what an earlier publish left (O3): "? Ticket note (not confirmed)".</summary>
    [ObservableProperty]
    public partial string TicketNoteLabel { get; set; } = "Ticket note";

    [ObservableProperty]
    public partial string TimeEntryLabel { get; set; } = "Time entry";

    /// <summary>The labels in words for a screen reader, which would read the "?" as "question mark".</summary>
    [ObservableProperty]
    public partial string TicketNoteName { get; set; } = "Ticket note";

    [ObservableProperty]
    public partial string TimeEntryName { get; set; } = "Time entry";

    [ObservableProperty]
    public partial string KbName { get; set; } = "KB article";

    /// <summary>An earlier publish left something on the ticket, or may have: the form shows the check (O3).</summary>
    [ObservableProperty]
    public partial Visibility EarlierVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial string EarlierPrompt { get; set; } = string.Empty;

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

    [ObservableProperty]
    public partial bool CanRetry { get; set; }

    /// <summary>"Retry Hudu": where Retry sends again (<see cref="PublishPanel.RetryLabel"/>).</summary>
    [ObservableProperty]
    public partial string RetryLabel { get; set; } = "Retry";

    /// <summary>Retry for a screen reader: each destination it sends again and where.</summary>
    [ObservableProperty]
    public partial string RetryName { get; set; } = "Retry";

    /// <summary>
    /// Something may already be on the ticket (a lost answer, a deadline, a session already published):
    /// the warning with its checkbox shows, and Publish, Ctrl+Enter and Retry wait for the box.
    /// </summary>
    [ObservableProperty]
    public partial Visibility CheckTicketVisibility { get; set; } = Visibility.Collapsed;

    /// <summary>The panel's <see cref="PublishPanel.CheckTheTicketFirst"/> while something may have landed, ticked or not.</summary>
    [ObservableProperty]
    public partial string CheckTicketPrompt { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CheckTicketLabel { get; set; } = string.Empty;

    /// <summary>The technician's "I checked the ticket". Spent on the next send, so it clears itself after.</summary>
    [ObservableProperty]
    public partial bool TicketChecked { get; set; }

    /// <summary>The checkbox is off while a send is on its way: a tick then would be about the answer before it.</summary>
    [ObservableProperty]
    public partial bool CanCheckTicket { get; set; } = true;

    /// <summary>`Ctrl+Enter`, and the button. Refused by the panel when blocked, whatever pressed it.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task PublishAsync() => _sending.RunAsync(PublishOnceAsync, SendingChanged);

    private async Task PublishOnceAsync()
    {
        if (!_panel.CanPublish || BlockedHere() is not null)
        {
            return;
        }

        CanCheckTicket = false;
        PublishError = string.Empty;
        try
        {
            await _panel.PublishAsync(_note(), _frames()).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The provider's words reach the technician through the result list; a throw that the
            // delegate did not turn into a result is one the list cannot show, so it is said here, and
            // in the envelope, which is what is on screen while there is no receipt.
            Summary = $"Publishing failed: {error.Message}";
            PublishError = Summary;
        }

        Refresh();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task RetryAsync() => _sending.RunAsync(RetryOnceAsync, SendingChanged);

    private async Task RetryOnceAsync()
    {
        // Something may already be on the ticket and the box is not ticked: the warning above says so.
        if (_panel.RetryBlockedBecause is { } check)
        {
            CheckTicketPrompt = check;
            return;
        }

        // Retry sends only what failed, so the minutes matter only when the time entry is among it.
        if (BlockedHere(minutesMatter: _panel.Failed.Contains(Destination.TimeEntry)) is { } reason)
        {
            Summary = reason;
            return;
        }

        CanCheckTicket = false;
        try
        {
            await _panel.RetryAsync(_note(), _frames()).ConfigureAwait(true);
        }
        finally
        {
            Refresh();
        }
    }

    /// <summary>
    /// The prompt's answer (ST-097 AC2): the chosen company is remembered by the backend and the article
    /// is sent again, on its own. A refusal leaves the prompt up with a line saying so, because the
    /// article is still not published and the technician should not have to guess whether it is.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task MapAndPublishAsync() => _sending.RunAsync(MapAndPublishOnceAsync, SendingChanged);

    private bool CanSend() => !_sending.Busy;

    private void SendingChanged()
    {
        PublishCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        MapAndPublishCommand.NotifyCanExecuteChanged();
    }

    private async Task MapAndPublishOnceAsync()
    {
        if (SelectedCompany is not { } choice || !_panel.NeedsMapping)
        {
            return;
        }

        // The article carries the frames too, so the ceiling holds here; it carries no minutes. And the
        // retry behind the mapping sends whatever failed, including one that may already be on the ticket.
        if ((_panel.RetryBlockedBecause ?? BlockedHere(minutesMatter: false)) is { } reason)
        {
            MappingError = reason;
            MappingErrorVisibility = Visibility.Visible;
            return;
        }

        CanMap = false;
        CanCheckTicket = false;
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

    /// <summary>"Open in ConnectWise" or "Open in Hudu". Only ever a link the provider handed back, and only a web link (<see cref="ExternalLinks.IsWebLink"/>).</summary>
    [RelayCommand]
    private static void OpenLink(Uri? link)
    {
        if (ExternalLinks.IsWebLink(link))
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
        // The picker stays open while the arrows move through the list; Enter, a click or Esc closes it.
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

    /// <summary>
    /// T-386: only digits that make a whole number of minutes reach the panel. Anything else (blank, a
    /// decimal, a sign, letters, a number too large) used to leave the panel on the previous value, so the
    /// time entry went out with a number the field did not show. Now Publish is blocked until it is fixed,
    /// and the field keeps what was typed.
    /// </summary>
    partial void OnMinutesChanged(string value)
    {
        var valid = int.TryParse(
            value,
            System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
            System.Globalization.CultureInfo.InvariantCulture,
            out var minutes);
        if (valid)
        {
            _panel.Minutes = minutes;
        }

        if (valid != _minutesValid)
        {
            _minutesValid = valid;
            RefreshBlock();
        }
    }

    /// <summary>The strip changed what goes out, so the frame ceiling may have been crossed either way (E-18).</summary>
    public void FramesChanged() => RefreshBlock();

    partial void OnTicketCheckedChanged(bool value)
    {
        _panel.TicketChecked = value;
        Refresh();
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

        // The envelope's To line: the ticket as a chip, why it was matched, and Change (Ctrl+T).
        ToValue = _panel.ToValue;
        TicketChipVisibility = TicketVisibility;
        NoTicketVisibility = _panel.Ticket is null ? Visibility.Visible : Visibility.Collapsed;
        TicketId = _panel.Ticket is { } bound ? $"#{bound.Id}" : string.Empty;
        TicketRest = _panel.Ticket is { } rest ? $"{rest.Summary} · {rest.Company}" : string.Empty;
        MatchedFrom = _panel.MatchedFrom ?? string.Empty;
        ChangeLabel = _panel.ChangeLabel;
        ChangeVisibility = _panel.HasIntegrations ? Visibility.Visible : Visibility.Collapsed;
        TimeNote = _panel.TimeNote;
        KbReason = _panel.KbReason ?? string.Empty;
        // A field holding something that is not a number keeps showing it: putting the panel's last good
        // value back would show a number the technician did not type, which is the T-386 problem again.
        if (_minutesValid)
        {
            Minutes = _panel.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        RoundingNote = _panel.RoundingNote;
        IsInternal = _panel.NoteType == NoteType.Internal;
        IsDiscussion = _panel.NoteType == NoteType.Discussion;
        TicketNote = _panel.TicketNote;
        TimeEntry = _panel.TimeEntry;
        KbArticle = _panel.KbArticle;
        // The Also chips. The article's reason is its own line under them, so the chip stays one line.
        (TicketNoteLabel, TicketNoteName) = Earlier(Destination.TicketNote, PublishPanel.LabelOf(Destination.TicketNote));
        (TimeEntryLabel, TimeEntryName) = Earlier(Destination.TimeEntry, PublishPanel.LabelOf(Destination.TimeEntry));
        (KbLabel, KbName) = Earlier(Destination.KbArticle, _panel.KbChipLabel);

        // O3: the check in the form, for a destination an earlier publish landed or may have landed. The
        // sentence stays while it shows; what is ticked only decides whether Publish waits for the box.
        EarlierVisibility = _panel.HasEarlier && _panel.Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // m-g: after the service's refusal sent the pane back here, its words lead, in the live region,
        // since the result list that would have said them is gone.
        const string Choice = "An earlier publish reached the ticket, or may have. Destinations marked already on the ticket, already in the knowledge base or not confirmed start unticked; sending one of them again needs the check below. The others go without it.";
        EarlierPrompt = !_panel.HasEarlier
            ? string.Empty
            : _panel.EarlierRefusal is { Length: > 0 } refusal ? $"{refusal} {Choice}" : Choice;
        RefreshBlock();
        PublishVisibility = _panel.HasIntegrations ? Visibility.Visible : Visibility.Collapsed;
        ConnectVisibility = _panel.HasIntegrations ? Visibility.Collapsed : Visibility.Visible;

        var showResults = _panel.Results.Count > 0;
        FormVisibility = showResults ? Visibility.Collapsed : Visibility.Visible;
        ResultsVisibility = showResults ? Visibility.Visible : Visibility.Collapsed;
        PublishedVisibility = _panel.Published ? Visibility.Visible : Visibility.Collapsed;

        // While the article waits on a mapping, the prompt is the retry: Retry alone would fail the same way.
        var needsMapping = _panel.NeedsMapping;
        var mayHaveLanded = _panel.NeedsTicketCheck;
        RetryVisibility = (_panel.PartiallyPublished || mayHaveLanded) && !needsMapping ? Visibility.Visible : Visibility.Collapsed;
        CanRetry = _panel.CanRetry;

        // The send resets the panel's confirmation, so the box follows it rather than the other way round.
        TicketChecked = _panel.TicketChecked;
        CanCheckTicket = !_panel.IsPublishing;
        CheckTicketVisibility = mayHaveLanded && _panel.Results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Kept up once the box is ticked: only the buttons change, so the live region never announces an
        // empty line and Retry's help text never goes blank.
        CheckTicketPrompt = mayHaveLanded ? PublishPanel.CheckTheTicketFirst : string.Empty;
        CheckTicketLabel = _panel.Ticket is { } ticket ? $"I checked ticket #{ticket.Id} and want to publish again" : "I checked the ticket and want to publish again";
        MappingVisibility = needsMapping ? Visibility.Visible : Visibility.Collapsed;
        MappingPrompt = _panel.MappingPrompt ?? string.Empty;
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
        RetryLabel = _panel.RetryLabel;
        RetryName = _panel.RetryName;
        Results.Clear();
        foreach (var row in _panel.Rows)
        {
            Results.Add(new ResultRow(row, _panel.DetailOf(row.Destination)));
        }

        RefreshReceipt(showResults);
    }

    /// <summary>The receipt's heading, facts and change line, and Next to check the first time it shows.</summary>
    private void RefreshReceipt(bool showResults)
    {
        if (showResults)
        {
            PickerVisibility = Visibility.Collapsed;
        }

        ReceiptHeading = _panel.ReceiptHeading ?? string.Empty;
        ReceiptSubline = _panel.ReceiptSubline ?? string.Empty;
        ReceiptTone = _panel.ReceiptState ?? ReceiptState.Sent;
        ReceiptGlyph = ReceiptTone switch
        {
            ReceiptState.Sent => "",
            ReceiptState.Partial => "",
            ReceiptState.NotConfirmed => "",
            _ => "",
        };
        ChangeNote = _panel.ChangeNote ?? string.Empty;

        // Sent says it in the heading; anything else, or a screenshot that did not attach, needs the line.
        SummaryVisibility = showResults && (!_panel.Published || _panel.Results.Any(r => r is { Ok: true, Kind: DestinationResult.Partial, Error.Length: > 0 }))
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReceiptFacts.Clear();
        foreach (var fact in _panel.ReceiptFacts)
        {
            ReceiptFacts.Add(fact);
        }

        if (showResults && !_nextAsked)
        {
            _nextAsked = true;
            _ = LoadNextAsync();
        }
    }

    /// <summary>History's rows, read once the receipt shows: the next session to check and the day so far.</summary>
    private async Task LoadNextAsync()
    {
        IReadOnlyList<Shared.Ipc.SessionRow>? rows = null;
        if (_next is not null)
        {
            try
            {
                rows = await _next.List(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // The receipt stands without its next session: the list is a convenience, never the result.
                rows = null;
            }
        }

        var next = NextToCheck.Pick(rows, _sessionId);
        _nextId = next?.Id;
        NextTitle = next is null ? string.Empty : NextToCheck.Title(next);
        NextMeta = next is null ? string.Empty : NextToCheck.Meta(next, TimeZoneInfo.Local);
        NextVisibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        NoNextVisibility = next is null ? Visibility.Visible : Visibility.Collapsed;
        if (rows is not null)
        {
            var today = NextToCheck.Today(rows, _sessionId, DateTimeOffset.Now, TimeZoneInfo.Local);
            TodayPublished = today.Published.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TodayLeft = today.LeftToCheck.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TodayVisibility = Visibility.Visible;
        }
    }

    /// <summary>Why Publish is disabled and whether it is. The panel's reasons come first; this pane's two follow.</summary>
    private void RefreshBlock()
    {
        var here = BlockedHere();
        var held = _heldBack();
        BlockedBecause = _panel.HasIntegrations ? held ?? _panel.BlockedBecause ?? here ?? string.Empty : string.Empty;
        BlockedVisibility = BlockedBecause.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CanPublish = _panel.CanPublish && here is null;
    }

    /// <summary>
    /// The two refusals this pane adds to the panel's, or null. The backend takes at most
    /// <see cref="PublishCommands.MaxFrames"/> frames and refuses the whole publish above that (E-18), and
    /// a time entry must carry the number the field shows (T-386).
    /// </summary>
    private string? BlockedHere(bool minutesMatter = true)
    {
        // First, and whatever else is true: the frames themselves are not settled.
        if (_heldBack() is { } held)
        {
            return held;
        }

        var frames = _frames().Count;
        if (frames > PublishCommands.MaxFrames)
        {
            return $"A publish can carry at most {PublishCommands.MaxFrames} frames, and {frames} are included. Exclude {frames - PublishCommands.MaxFrames} in the filmstrip.";
        }

        return minutesMatter && _panel.TimeEntry && !_minutesValid
            ? "Enter the time entry's minutes as a whole number"
            : null;
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

    /// <summary>A destination's label and its name for a screen reader, with what an earlier publish left (O3).</summary>
    private (string Label, string Name) Earlier(Destination destination, string label) => _panel.EarlierFor(destination) switch
    {
        EarlierOutcome.Landed when destination == Destination.KbArticle => ($"{label}  (already in the knowledge base)", $"{label}, already in the knowledge base"),
        EarlierOutcome.Landed => ($"{label}  (already on the ticket)", $"{label}, already on the ticket"),
        EarlierOutcome.Unknown => ($"? {label}  (not confirmed)", $"{label}, not confirmed"),
        _ => (label, label),
    };
}
