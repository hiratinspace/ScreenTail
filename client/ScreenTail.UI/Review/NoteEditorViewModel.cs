using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Review;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Schema;

namespace ScreenTail.UI.Review;

/// <summary>A masked rectangle, in the frame's own pixels, drawn over the step's thumbnail.</summary>
public sealed record MaskBox(System.Windows.Thickness Offset, double Width, double Height);

/// <summary>
/// A step's own frame (Spec v0.6.0 §5 S3: "Each step row carries its own frame and quote"), drawn on the
/// step's row as its thumbnail. The picture is the strip's: the same <see cref="FrameItem"/>, loaded at
/// thumbnail size from what the store holds once redacted (INV-1), so a blur in the strip shows here too.
/// </summary>
/// <remarks>
/// Was the v0.5 "frame 2" chip, which highlighted the filmstrip and jumped on click. It keeps the frame's
/// strip label for its spoken name, so the strip and the step call one frame by one name.
/// </remarks>
public sealed partial class FrameChip : ObservableObject
{
    internal FrameChip(string id, string label, FrameItem? item)
    {
        Id = id;
        Label = label;
        Item = item;
        Masks = MasksOf(item);
    }

    public string Id { get; }

    /// <summary>
    /// The frame's place in the session, "frame 2", as the strip shows it: not its id, which is a fact about
    /// the database and not something anyone can find on the screen.
    /// </summary>
    public string Label { get; }

    /// <summary>The strip's frame, for the thumbnail and its state. Null where there is no strip (the note's own harness).</summary>
    public FrameItem? Item { get; }

    /// <summary>Where it goes, as the ticket says it: "screenshot 2", "not attached" or "withheld".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpokenName))]
    private string _place = "not attached";

    /// <summary>Still in the strip. A deleted frame leaves its step's row too, and comes back if the delete is undone.</summary>
    [ObservableProperty]
    private bool _present = true;

    /// <summary>The step this frame is on, for the button's name: two steps can show one frame.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpokenName))]
    private string _step = "Step";

    /// <summary>"Step 2 screenshot, frame 2, screenshot 2": what the button is called, unique on the note.</summary>
    public string SpokenName => $"{Step} screenshot, {Label}, {Place}";

    /// <summary>The frame's size in its own pixels, which the masks are measured in.</summary>
    public double FrameWidth => Math.Max(1, Item?.Cell.Frame.Width ?? 16);

    public double FrameHeight => Math.Max(1, Item?.Cell.Frame.Height ?? 9);

    /// <summary>
    /// Every masked region, drawn over the thumbnail as an opaque box (Spec v0.6.0 §5 S3: "A masked part of
    /// a frame stays masked"). The picture is already painted over by the redaction worker; the box marks
    /// where, so no scale of thumbnail can show what was there.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaskedLabel))]
    private IReadOnlyList<MaskBox> _masks;

    /// <summary>"2 masked" under the thumbnail, or empty.</summary>
    public string MaskedLabel => Masks.Count == 0 ? string.Empty : $"{Masks.Count} masked";

    internal void Update(IReadOnlyList<string> attached, bool present)
    {
        Present = present;
        var place = NoteLanding.ScreenshotPlaces([Id], attached);
        Place = NoteLanding.FrameState(place, Item?.Included ?? place is not null, Item?.Cell.Withheld ?? false);
        Masks = MasksOf(Item);
        OnPropertyChanged(nameof(FrameWidth));
        OnPropertyChanged(nameof(FrameHeight));
    }

    private static IReadOnlyList<MaskBox> MasksOf(FrameItem? item) =>
        item is null ? [] : [.. item.Cell.Frame.MaskedRegions.Select(r => new MaskBox(new System.Windows.Thickness(r.X, r.Y, 0, 0), r.Width, r.Height))];
}

/// <summary>
/// One step, adapted for binding. It holds no editing rules: every mutation goes back through
/// <see cref="NoteDraft"/> so there is exactly one place the rules live and it is the one with the tests.
/// </summary>
public sealed partial class StepRow : ObservableObject
{
    private readonly NoteDraft _note;
    private readonly NoteStep _step;

    internal StepRow(NoteDraft note, NoteStep step, IReadOnlyList<FrameChip> frames, string? quote, string? time)
    {
        _note = note;
        _step = step;
        Frames = frames;
        Quote = quote;
        Time = time;
        _text = step.Text;
    }

    public string Id => _step.Id;

    public IReadOnlyList<FrameChip> Frames { get; }

    /// <summary>The transcript line this step came from, shown in mono under the text; null when there is none.</summary>
    public string? Quote { get; }

    public bool HasQuote => Quote is not null;

    /// <summary>When it happened, "14:04", on the session's clock; null for a step the technician added.</summary>
    public string? Time { get; }

    /// <summary>Whether the ⚠ marker is up. Spec §7: never the only signal: the words "Inferred from screen only" are beside it.</summary>
    public bool NeedsVerification => _step.NeedsVerification;

    /// <summary>Spec §5 S3, verbatim.</summary>
    public string VerificationTooltip { get; } = "Inferred from screen only. Please verify.";

    /// <summary>The line an inferred step carries (Spec v0.6.0 §5 S3).</summary>
    public string InferredLine { get; } = NoteLanding.Inferred;

    [ObservableProperty]
    private string _text;

    /// <summary>"Step 2": the step box's name for a screen reader, kept in step with the list's order.</summary>
    [ObservableProperty]
    private string _place = "Step";

    /// <summary>"2": the number the ticket gives the step ("2. Started the service").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmName))]
    [NotifyPropertyChangedFor(nameof(EditName))]
    private string _number = string.Empty;

    /// <summary>A transcript line picked in the timeline is this step's: the row is marked, as the strip's frame was.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>"Looks right, step 3": the label first (WCAG 2.5.3), then which step, since every inferred row has one.</summary>
    public string ConfirmName => $"Looks right, step {Number}";

    public string EditName => $"Edit step {Number}";

    partial void OnPlaceChanged(string value)
    {
        foreach (var frame in Frames)
        {
            frame.Step = value;
        }
    }

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
    private readonly Func<string?>? _discardBlocked;
    private readonly OneAtATime _discarding = new();
    private readonly Func<CancellationToken, Task>? _retryDraft;
    private readonly bool _persists;
    private readonly Dictionary<string, string> _quotes;
    private readonly Dictionary<string, string> _frameLabels;
    private readonly Action<string?>? _showFrame;
    private readonly FilmstripViewModel? _strip;
    private bool _disposed;

    /// <param name="write">Persists the note. Called by the autosave, one write at a time.</param>
    /// <param name="confirm">Shows the typed-confirmation dialog and returns whether it was satisfied.</param>
    /// <param name="discard">Deletes the session and its raw data. Null in the render harness.</param>
    /// <param name="retryDraft">Asks for the note again after a failed draft (ST-061).</param>
    /// <param name="showFrame">Opens a step's frame when its thumbnail is pressed (T-165): selected in the strip and enlarged.</param>
    /// <param name="draftPending">The draft is queued for the backend (ST-013): on its way, not failed.</param>
    /// <param name="discardBlocked">
    /// Why Discard may not be asked about right now, or null when it may: a session (this one or another)
    /// is being captured, or the service is not answering. Asked before the dialog, because Spec §4 allows
    /// no modal during a recording, and again once it is answered.
    /// </param>
    /// <param name="strip">
    /// The session's screenshots, whose frames each step row shows (Spec v0.6.0 §5 S3) and whose included
    /// list says what each is called on the ticket. Null in the note's own harness, which shows none.
    /// </param>
    public NoteEditorViewModel(
        Session session,
        Func<DraftNote, CancellationToken, Task>? write = null,
        Func<TypedConfirmation, bool>? confirm = null,
        Func<CancellationToken, Task>? discard = null,
        Func<CancellationToken, Task>? retryDraft = null,
        bool offline = false,
        TimeProvider? time = null,
        Action<string?>? showFrame = null,
        bool draftPending = false,
        Func<string?>? discardBlocked = null,
        FilmstripViewModel? strip = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _showFrame = showFrame;
        _strip = strip;
        _confirm = confirm ?? (_ => false);
        _discard = discard;
        _discardBlocked = discardBlocked;
        DiscardHint = SessionDiscard.Hint(discardBlocked?.Invoke());
        _retryDraft = retryDraft;
        _persists = write is not null;

        // Spec S3 draws the draft-failed state as the pane saying so, with the timeline and screenshots
        // still there. An empty editor over a session that has no note would look like a note nobody wrote.
        HasDraft = session.Draft is not null;
        DraftPending = !HasDraft && draftPending;
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
        PublishBlockedBecause = NoteBanners.PublishBlockedBecause(session, ticketChosen: false, offline, draftPending);
        _problem = _note.Problem;
        _result = _note.Result;
        _followUps = _note.FollowUpsText();
        Steps = [.. _note.Steps.Select(RowFor)];

        // Each step box is named by its place, so a screen reader says "Step 2" rather than the same words
        // for every step, and the names follow a reorder, an added step and a removed one (ST-084).
        Steps.CollectionChanged += (_, _) => Number();
        Number();

        // What each frame is called on the ticket, whether it is still there, and the masking count all
        // follow the strip: Space, Del and its undo, and a blur that adds a region.
        if (strip is not null)
        {
            strip.PropertyChanged += OnStripChanged;
            strip.Items.CollectionChanged += OnStripItemsChanged;
            foreach (var item in strip.Items.OfType<FrameItem>())
            {
                item.PropertyChanged += OnFrameChanged;
            }
        }

        RefreshFrames();
    }

    private void Number()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].Place = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Step {i + 1}");
            Steps[i].Number = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The note shows the strip's frames, and "All screenshots" opens it.</summary>
    public bool HasStrip => _strip is not null;

    /// <summary>"3 of 3 included", on the button that opens the whole strip.</summary>
    public string StripHeader => _strip?.Header ?? string.Empty;

    /// <summary>"All screenshots, 3 of 3 included": the button's name, its label first (WCAG 2.5.3).</summary>
    public string AllScreenshotsName => $"All screenshots, {StripHeader}";

    /// <summary>The quiet line over the note (Spec v0.6.0 §5 S3).</summary>
    public string Guide { get; } = NoteLanding.Guide;

    /// <summary>What an empty section says: it is left off the ticket.</summary>
    public string EmptySection { get; } = NoteLanding.EmptySection;

    /// <summary>Under the note, before the save state: "2 items masked, none left to review" (Spec v0.6.0 §5 S3).</summary>
    public string MaskingLabel { get; private set; } = "Nothing masked";

    /// <summary>Opens the whole strip in the note's place: the frames no step shows, and Del with its undo.</summary>
    [RelayCommand]
    private void ShowAllScreenshots()
    {
        if (_strip is { } strip)
        {
            strip.Browsing = true;
        }
    }

    /// <summary>`Space` on a step's frame: in or out of the publish, through the strip's own command, as `Space` there.</summary>
    [RelayCommand]
    private async Task ToggleFrameAsync(FrameChip? chip)
    {
        if (_strip is { } strip && chip?.Item is { } item && strip.Items.Contains(item))
        {
            await strip.ToggleIncludeCommand.ExecuteAsync(item).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// A transcript line picked in the timeline: the step that frame is on is marked, since the strip that
    /// used to show the selection is no longer beside the note.
    /// </summary>
    public void Highlight(string? frameId)
    {
        foreach (var row in Steps)
        {
            row.IsCurrent = frameId is not null && row.Frames.Any(frame => frame.Id == frameId);
        }
    }

    private void OnStripChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilmstripViewModel.Header) or nameof(FilmstripViewModel.PublishHeldBecause))
        {
            RefreshFrames();
        }
    }

    private void OnStripItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (var item in e.NewItems?.OfType<FrameItem>() ?? [])
        {
            item.PropertyChanged -= OnFrameChanged;
            item.PropertyChanged += OnFrameChanged;
        }

        RefreshFrames();
    }

    private void OnFrameChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FrameItem.Cell) or nameof(FrameItem.Included))
        {
            RefreshFrames();
        }
    }

    /// <summary>Each step's frame as the strip has it now, and the masking count over what goes out.</summary>
    private void RefreshFrames()
    {
        if (_strip is not { } strip)
        {
            return;
        }

        var attached = strip.IncludedFrameIds;
        var present = strip.Items.OfType<FrameItem>().ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var chip in Steps.SelectMany(row => row.Frames))
        {
            chip.Update(attached, present.ContainsKey(chip.Id));
        }

        var going = attached.Where(present.ContainsKey).Select(id => present[id].Cell.Frame);
        var onSteps = Steps.SelectMany(row => row.Frames).Select(frame => frame.Id).ToHashSet(StringComparer.Ordinal);
        MaskingLabel = NoteLanding.Masking(going, onSteps);
        OnPropertyChanged(nameof(MaskingLabel));
        OnPropertyChanged(nameof(StripHeader));
        OnPropertyChanged(nameof(AllScreenshotsName));
    }

    public bool HasDraft { get; }

    /// <summary>The note as it is now, edited, for publishing (ST-078). Never the draft as it arrived.</summary>
    public DraftNote CurrentNote => _note.ToSchema();

    /// <summary>The other half, for the pane that replaces the editor. Two properties rather than an
    /// inverting converter, because a converter parameter that silently does nothing is how a pane ends up
    /// showing both states at once.</summary>
    public bool DraftFailed => !HasDraft && !DraftPending;

    /// <summary>Spec v0.4.1 Q4: what the pane says instead of an editor when drafting failed.</summary>
    public string DraftFailedMessage { get; } = "We couldn't draft this session.";

    /// <summary>
    /// The draft is on its way from the backend (ST-013). Review used to say drafting had failed and offer
    /// Retry, which is wrong while the note is still coming.
    /// </summary>
    public bool DraftPending { get; }

    /// <summary>What the pane says instead of an editor while the draft is on its way.</summary>
    public string DraftPendingMessage { get; } = "Drafting this session. The note appears here when it arrives.";

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
        SaveStatus.Failed => "Not saved: retrying",

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
        // The shell disposes the pane as it takes it off the screen, and the view's timer can tick once
        // more before the view is unloaded. A retry due then would reach the autosave's disposed gate.
        if (_disposed)
        {
            return;
        }

        await _save.TickAsync(ct).ConfigureAwait(true);
        OnPropertyChanged(nameof(SaveLabel));
    }

    /// <summary>
    /// Writes anything outstanding now, without waiting for the autosave's pause (T-169). The pane calls it
    /// when it is unloaded. Does nothing once disposed.
    /// </summary>
    public async Task FlushAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _save.FlushAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(SaveLabel));
    }

    /// <summary>Writes anything outstanding. Call before the window closes. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _note.Changed -= OnNoteChanged;
        if (_strip is { } strip)
        {
            strip.PropertyChanged -= OnStripChanged;
            strip.Items.CollectionChanged -= OnStripItemsChanged;
            foreach (var item in strip.Items.OfType<FrameItem>())
            {
                item.PropertyChanged -= OnFrameChanged;
            }
        }

        await _save.FlushAsync().ConfigureAwait(false);
        _save.Dispose();
    }

    /// <summary>A step's frame: opens it (selected in the strip and enlarged), or clears the strip's selection when it is not there.</summary>
    [RelayCommand]
    private void ShowFrame(string? frameId) => _showFrame?.Invoke(frameId);

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
    /// The header's Discard (Spec §5 S3). Irreversible, so it goes through the typed gate first (Spec §3)
    /// and the caller's <c>discard</c> asks the service to throw the session away by name. Disabled when
    /// nothing is wired to do it, as in the render harness: a Discard that looks pressable and does nothing
    /// is a lie about the one irreversible action on the screen.
    /// </summary>
    /// <remarks>
    /// One typed question at a time (<see cref="OneAtATime"/>). The dialog is shown before the first
    /// await, inside a nested message loop where a queued invoke can arrive, and the command does not yet
    /// count as running then, so without the guard a second invoke opened a second dialog.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private Task DiscardAsync() => _discarding.RunAsync(DiscardOnceAsync, DiscardCommand.NotifyCanExecuteChanged);

    private async Task DiscardOnceAsync()
    {
        if (_discard is null)
        {
            return;
        }

        // No dialog over a session still being captured (Spec §4): the reason goes beside the button.
        if (_discardBlocked?.Invoke() is { } blocked)
        {
            SetDiscardError(blocked);
            return;
        }

        if (!_confirm(TypedConfirmation.ForDiscard()))
        {
            return;
        }

        // Asked again once the dialog is answered, as the mark and History's bulk discard do: the service
        // starts sessions by itself, and one may have begun, or the service gone, while DISCARD was typed.
        // Nothing is sent, and the autosave is never stopped.
        if (_discardBlocked?.Invoke() is { } since)
        {
            SetDiscardError(since);
            return;
        }

        // Before the service is asked, not after. The note would otherwise be written once more by a tick
        // or a closing flush, and put a full readable note back into a session whose audit log says a
        // human threw it away; there is nothing in the note that a discard was meant to keep.
        SetDiscardError(null);
        _save.Stop();
        try
        {
            await _discard(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A discard that fails must not look like one that worked: the technician is standing in front
            // of a customer believing the screenshots are gone. Nothing was thrown away, so the note is
            // still theirs, and what they type next is saved again.
            _save.Resume();
            SetDiscardError(error.Message);
        }
    }

    /// <summary>Off while the session is being captured too, with the reason beside it (<see cref="DiscardHint"/>).</summary>
    private bool CanDiscard() => !_discarding.Busy && _discard is not null && DiscardHint is null;

    /// <summary>
    /// Asks again whether Discard is held, and says so beside it: the shell calls this when capture
    /// changes, so the button comes back when the session stops. A "still being captured" refusal from a
    /// press before then goes with it.
    /// </summary>
    public void RefreshDiscard()
    {
        var hint = SessionDiscard.Hint(_discardBlocked?.Invoke());
        if (hint is null && SessionDiscard.SaysCapturing(DiscardError))
        {
            SetDiscardError(null);
        }

        if (!string.Equals(hint, DiscardHint, StringComparison.Ordinal))
        {
            DiscardHint = hint;
            OnPropertyChanged(nameof(DiscardHint));
            OnPropertyChanged(nameof(DiscardHelp));
            DiscardCommand.NotifyCanExecuteChanged();
        }

        OnPropertyChanged(nameof(DiscardHintVisible));
    }

    private void SetDiscardError(string? error)
    {
        DiscardError = error;
        OnPropertyChanged(nameof(DiscardError));
        OnPropertyChanged(nameof(DiscardFailed));
        OnPropertyChanged(nameof(DiscardHintVisible));
    }

    /// <summary>
    /// Why Discard is off, beside it, or null: "Stop the session to discard" while it is being captured.
    /// Visible text, not a tooltip a keyboard user cannot reach on a disabled button (Spec v0.4.3).
    /// </summary>
    public string? DiscardHint { get; private set; }

    /// <summary>Shown while it applies, unless the longer refusal beside the button already says it.</summary>
    public bool DiscardHintVisible => DiscardHint is not null && !DiscardFailed;

    /// <summary>The button's help text: what it does, and why it is off when it is.</summary>
    public string DiscardHelp => DiscardHint is { } hint
        ? $"{hint}. Deletes the screenshots, transcript and note from this device. You will be asked to type DISCARD."
        : "Deletes the screenshots, transcript and note from this device. You will be asked to type DISCARD.";

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
        var items = _strip?.Items.OfType<FrameItem>().ToDictionary(item => item.Id, StringComparer.Ordinal);
        var frames = step.FrameRefs
            .Where(_frameLabels.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .Select(id => new FrameChip(id, _frameLabels[id], items?.GetValueOrDefault(id)))
            .ToList();

        // One line, and only when it exists: Spec shows the step's own words quoted under it, not a
        // paragraph of transcript that happens to overlap.
        var quote = step.TranscriptRefs
            .Select(id => _quotes.GetValueOrDefault(id))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        if (_strip is { } strip)
        {
            foreach (var chip in frames)
            {
                chip.Update(strip.IncludedFrameIds, items!.ContainsKey(chip.Id));
            }
        }

        return new StepRow(_note, step, frames, quote is null ? null : $"“{quote}”", NoteLanding.StepTime(_session, step.FrameRefs, step.TranscriptRefs));
    }

    private void OnNoteChanged()
    {
        _save.Touch();
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(UnverifiedLabel));
    }
}
