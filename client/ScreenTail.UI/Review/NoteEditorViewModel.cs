using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;

namespace ScreenTail.UI.Review;

/// <summary>One frame chip under a step: "frame 2", which highlights the filmstrip and jumps on click.</summary>
/// <param name="Label">What the technician reads. The frame's position in the session, not its id — an id
/// is a fact about the database and "frame f-0002" is not something anyone can find on the screen.</param>
public sealed record FrameChip(string Id, string Label);

/// <summary>
/// One step, adapted for binding. It holds no editing rules: every mutation goes back through
/// <see cref="NoteDraft"/> so there is exactly one place the rules live and it is the one with the tests.
/// </summary>
public sealed partial class StepRow : ObservableObject
{
    private readonly NoteDraft _note;
    private readonly NoteStep _step;

    internal StepRow(NoteDraft note, NoteStep step, IReadOnlyList<FrameChip> frames, string? quote)
    {
        _note = note;
        _step = step;
        Frames = frames;
        Quote = quote;
        _text = step.Text;
    }

    public string Id => _step.Id;

    public IReadOnlyList<FrameChip> Frames { get; }

    /// <summary>The transcript line this step came from, shown in mono under the text; null when there is none.</summary>
    public string? Quote { get; }

    public bool HasQuote => Quote is not null;

    /// <summary>Whether the ⚠ marker is up. Spec §7: never the only signal — the word "Inferred" is beside it.</summary>
    public bool NeedsVerification => _step.NeedsVerification;

    /// <summary>Spec §5 S3, verbatim.</summary>
    public string VerificationTooltip { get; } = "Inferred from screen only — please verify";

    [ObservableProperty]
    private string _text;

    partial void OnTextChanged(string value)
    {
        _note.SetStepText(Id, value);
        OnPropertyChanged(nameof(NeedsVerification));
    }

    internal void Confirm()
    {
        _note.ConfirmStep(Id);
        OnPropertyChanged(nameof(NeedsVerification));
    }
}

/// <param name="Glyph">Spec §7: the kind has a shape as well as a colour, so the two warnings are not
/// distinguishable by colour alone.</param>
public sealed record BannerRow(string Text, bool IsWarning, string Glyph, string Kind);

/// <summary>
/// The note pane (ST-074, Spec §5 S3 left pane).
///
/// The editing rules, the save timing, the banner wording and the discard gate all live in
/// <c>ScreenTail.Core.Review</c>, where they are tested without WPF and without a Windows machine
/// (ADR-0002). What is left here is binding, keyboard routing and the two things that genuinely need a
/// window: focusing a step after it is created, and asking before discarding.
/// </summary>
public sealed partial class NoteEditorViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Session _session;
    private readonly NoteDraft _note;
    private readonly AutoSave _save;
    private readonly Func<TypedConfirmation, bool> _confirm;
    private readonly Func<CancellationToken, Task>? _discard;
    private readonly Func<CancellationToken, Task>? _retryDraft;
    private readonly bool _persists;
    private readonly Dictionary<string, string> _quotes;
    private readonly Dictionary<string, string> _frameLabels;

    /// <param name="write">Persists the note. Called by the autosave, one write at a time.</param>
    /// <param name="confirm">Shows the typed-confirmation dialog and returns whether it was satisfied.</param>
    /// <param name="discard">Deletes the session and its raw data. Null in the render harness.</param>
    /// <param name="retryDraft">Asks for the note again after a failed draft (ST-061).</param>
    public NoteEditorViewModel(
        Session session,
        Func<DraftNote, CancellationToken, Task>? write = null,
        Func<TypedConfirmation, bool>? confirm = null,
        Func<CancellationToken, Task>? discard = null,
        Func<CancellationToken, Task>? retryDraft = null,
        bool offline = false,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _confirm = confirm ?? (_ => false);
        _discard = discard;
        _retryDraft = retryDraft;
        _persists = write is not null;

        // Spec S3 draws the draft-failed state as the pane saying so, with the timeline and screenshots
        // still there. An empty editor over a session that has no note would look like a note nobody wrote.
        HasDraft = session.Draft is not null;
        _note = new NoteDraft(session.Draft ?? EmptyDraft);

        _quotes = session.Transcript.ToDictionary(segment => segment.Id, segment => segment.Text, StringComparer.Ordinal);
        _frameLabels = session.Frames
            .Select((frame, index) => (frame.Id, Label: $"frame {index + 1}"))
            .ToDictionary(pair => pair.Id, pair => pair.Label, StringComparer.Ordinal);

        _save = new AutoSave(
            ct => write is null ? Task.CompletedTask : write(_note.ToSchema(), ct),
            () => _note.Revision,
            time);
        _note.Changed += OnNoteChanged;

        Banners = [.. NoteBanners.For(session, offline).Select(Row)];
        PublishBlockedBecause = NoteBanners.PublishBlockedBecause(session, ticketChosen: false, offline);
        _problem = _note.Problem;
        _result = _note.Result;
        _followUps = _note.FollowUpsText();
        Steps = [.. _note.Steps.Select(RowFor)];
    }

    public bool HasDraft { get; }

    /// <summary>The other half, for the pane that replaces the editor. Two properties rather than an
    /// inverting converter, because a converter parameter that silently does nothing is how a pane ends up
    /// showing both states at once.</summary>
    public bool DraftFailed => !HasDraft;

    /// <summary>Spec v0.4.1 Q4: what the pane says instead of an editor when drafting failed.</summary>
    public string DraftFailedMessage { get; } = "We couldn't draft this session.";

    public IReadOnlyList<BannerRow> Banners { get; }

    public string? PublishBlockedBecause { get; }

    public ObservableCollection<StepRow> Steps { get; }

    /// <summary>
    /// Bottom-left of the pane, in <c>text.muted</c> (Spec §5 S3). Empty when nothing is persisting the
    /// note, which is the render harness: an editor that says "Saved" about a note going nowhere is
    /// exactly the lie the indicator exists to prevent, and it would have been the first thing anyone
    /// looking at the screenshots believed.
    /// </summary>
    public string SaveLabel => !_persists ? string.Empty : _save.Status switch
    {
        SaveStatus.Saved => "Saved",
        SaveStatus.Failed => "Not saved — retrying",

        // Pending and Saving are one word on purpose. The difference is ours, not the technician's, and a
        // third label would only invite them to wonder which one means their text is safe.
        _ => "Saving…",
    };

    /// <summary>The footer count, so a technician knows how much is left to check before publishing.</summary>
    public string UnverifiedLabel => _note.UnverifiedSteps switch
    {
        0 => "Every step checked",
        1 => "1 step still inferred",
        var many => $"{many} steps still inferred",
    };

    [ObservableProperty]
    private string _problem;

    [ObservableProperty]
    private string _result;

    [ObservableProperty]
    private string _followUps;

    /// <summary>Call on a UI timer. Cheap when there is nothing to write.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        await _save.TickAsync(ct).ConfigureAwait(true);
        OnPropertyChanged(nameof(SaveLabel));
    }

    /// <summary>Writes anything outstanding. Call before the window closes.</summary>
    public async ValueTask DisposeAsync()
    {
        _note.Changed -= OnNoteChanged;
        await _save.FlushAsync().ConfigureAwait(false);
        _save.Dispose();
    }

    partial void OnProblemChanged(string value) => _note.SetProblem(value);

    partial void OnResultChanged(string value) => _note.SetResult(value);

    partial void OnFollowUpsChanged(string value) => _note.SetFollowUpsText(value);

    /// <summary>The Retry on the draft-failed pane. Null until ST-061 is wired in; the button is there
    /// because Spec v0.4.1 Q4 makes it the only way forward, and a pane that says what went wrong and
    /// offers nothing is a dead end.</summary>
    [RelayCommand(CanExecute = nameof(CanRetryDraft))]
    private async Task RetryDraftAsync()
    {
        if (_retryDraft is { } retry)
        {
            await retry(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private bool CanRetryDraft() => _retryDraft is not null;

    /// <summary>`Ctrl+S`. The indicator is the only feedback, which is why it updates before returning.</summary>
    [RelayCommand]
    private async Task SaveNowAsync()
    {
        await _save.FlushAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(SaveLabel));
    }

    /// <summary>`Alt+C` on the focused step.</summary>
    [RelayCommand]
    private void ConfirmStep(StepRow? row)
    {
        row?.Confirm();
        OnPropertyChanged(nameof(UnverifiedLabel));
    }

    /// <summary>`Alt+↑` / `Alt+↓`. Moves the row with the step so the focus travels with it.</summary>
    [RelayCommand]
    private void MoveStep(object? parameter)
    {
        if (parameter is not (StepRow row, int delta) || !_note.MoveStep(row.Id, delta))
        {
            return;
        }

        var at = Steps.IndexOf(row);
        Steps.Move(at, at + delta);
    }

    /// <summary>`Enter` at the end of a step. Returns the new row so the view can put the caret in it.</summary>
    public StepRow AddStepAfter(StepRow? row)
    {
        var step = _note.InsertStepAfter(row?.Id);
        var added = RowFor(step);
        Steps.Insert(row is null ? Steps.Count : Steps.IndexOf(row) + 1, added);
        return added;
    }

    /// <summary>`Backspace` on an empty step. Whether it may go is <see cref="NoteDraft"/>'s call.</summary>
    /// <returns>Whether the row was removed, so the view knows whether to move the caret.</returns>
    public bool RemoveStep(StepRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!_note.DeleteStep(row.Id))
        {
            return false;
        }

        Steps.Remove(row);
        OnPropertyChanged(nameof(UnverifiedLabel));
        return true;
    }

    /// <summary>
    /// The header's Discard. Irreversible, so it goes through the typed gate first (Spec §3) and the
    /// caller's <c>discard</c> deletes the raw data and writes the audit row.
    /// </summary>
    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (_discard is null || !_confirm(TypedConfirmation.ForDiscard()))
        {
            return;
        }

        // Before the store call, not after. The session row survives a discard on purpose, so a tick or a
        // closing flush landing afterwards would put a full readable note back into a session whose audit
        // log says a human threw it away — and there is nothing in the note that a discard was meant to
        // keep.
        _save.Stop();
        try
        {
            await _discard(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A discard that fails must not look like one that worked: the technician is standing in front
            // of a customer believing the screenshots are gone.
            DiscardError = error.Message;
            OnPropertyChanged(nameof(DiscardError));
            OnPropertyChanged(nameof(DiscardFailed));
        }
    }

    /// <summary>Why the discard did not happen, or null. Shown beside the button, never as a dialog.</summary>
    public string? DiscardError { get; private set; }

    public bool DiscardFailed => DiscardError is not null;

    private static DraftNote EmptyDraft => new()
    {
        Problem = string.Empty,
        Steps = [],
        Result = string.Empty,
        FollowUps = [],
        SuggestedTitle = string.Empty,
        SuggestedTimeMinutes = 0,
        KbCandidate = false,
        KbReason = string.Empty,
        Source = DraftSource.Local,
        PromptVersion = "none",
    };

    private static BannerRow Row(Banner banner) => banner.Kind == BannerKind.Warning
        ? new BannerRow(banner.Text, true, "⚠", "Warning")
        : new BannerRow(banner.Text, false, "ⓘ", "Note");

    private StepRow RowFor(NoteStep step)
    {
        var frames = step.FrameRefs
            .Where(_frameLabels.ContainsKey)
            .Select(id => new FrameChip(id, _frameLabels[id]))
            .ToList();

        // One line, and only when it exists: Spec shows the step's own words quoted under it, not a
        // paragraph of transcript that happens to overlap.
        var quote = step.TranscriptRefs
            .Select(id => _quotes.GetValueOrDefault(id))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        return new StepRow(_note, step, frames, quote is null ? null : $"“{quote}”");
    }

    private void OnNoteChanged()
    {
        _save.Touch();
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(UnverifiedLabel));
    }
}
