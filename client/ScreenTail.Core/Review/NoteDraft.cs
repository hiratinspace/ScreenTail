using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>
/// One step of the note while it is being edited (Spec §5 S3, left pane).
///
/// <see cref="Confidence"/> is what the draft said and never changes: how a step was arrived at and
/// whether a human has since checked it are different facts. <see cref="Confirmed"/> records the second.
/// </summary>
public sealed class NoteStep
{
    private string _text;

    internal NoteStep(string id, DraftStep step)
    {
        Id = id;
        _text = step.Text;
        Confidence = step.Confidence;
        Confirmed = step.Confirmed ?? false;
        FrameRefs = [.. step.FrameRefs];
        TranscriptRefs = step.TranscriptRefs is null ? [] : [.. step.TranscriptRefs];
    }

    internal NoteStep(string id)
    {
        Id = id;
        _text = string.Empty;
        // A step the technician typed is theirs, so there is nothing to mark as inferred.
        Confidence = StepConfidence.High;
        Confirmed = true;
        FrameRefs = [];
        TranscriptRefs = [];
    }

    /// <summary>
    /// Identity for the editor, stable across reorder and unrelated to position. The schema has no step
    /// id, so this is assigned on load and never persisted — two loads of the same note give different
    /// ids, which is fine because nothing outside one editing session refers to them.
    /// </summary>
    public string Id { get; }

    public string Text => _text;

    /// <summary>What the draft claimed, for as long as the step exists. Not changed by confirming.</summary>
    public StepConfidence Confidence { get; }

    public bool Confirmed { get; private set; }

    /// <summary>The frames this step cites, in the order the draft gave them (Spec: "frame 2" chips).</summary>
    public IReadOnlyList<string> FrameRefs { get; }

    public IReadOnlyList<string> TranscriptRefs { get; }

    /// <summary>
    /// Whether Review shows the ⚠ marker. Spec: it clears when the step is edited or explicitly
    /// confirmed, so both routes set <see cref="Confirmed"/> — a technician who rewrote a sentence has
    /// already done the checking the marker was asking for.
    /// </summary>
    public bool NeedsVerification => Confidence == StepConfidence.Low && !Confirmed;

    internal bool SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == _text)
        {
            return false;
        }

        _text = text;
        Confirmed = true;
        return true;
    }

    internal bool Confirm()
    {
        if (Confirmed)
        {
            return false;
        }

        Confirmed = true;
        return true;
    }

    internal DraftStep ToSchema() => new()
    {
        Text = _text,
        Confidence = Confidence,
        // Only written once it is true, so a note nobody touched serialises exactly as the draft produced
        // it and the fixtures do not all gain a field the model never sets.
        Confirmed = Confirmed ? true : null,
        FrameRefs = FrameRefs,
        TranscriptRefs = TranscriptRefs.Count == 0 ? null : TranscriptRefs,
    };
}

/// <summary>
/// The note as Review edits it (ST-074), separated from WPF so the rules can be tested without a Windows
/// machine (ADR-0002). It owns no persistence: every mutation bumps <see cref="Revision"/> and raises
/// <see cref="Changed"/>, and <see cref="AutoSave"/> decides when that becomes a write.
///
/// Single-threaded by design — the editor is the UI thread and nothing else touches it. That is why there
/// is no lock here, unlike <c>ShellState</c>, which the pipe thread writes to.
/// </summary>
public sealed class NoteDraft
{
    private readonly List<NoteStep> _steps = [];
    private readonly List<string> _followUps = [];
    private readonly DraftNote _origin;
    private int _nextId;
    private string _problem;
    private string _result;
    private string _title;

    public NoteDraft(DraftNote draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _origin = draft;
        _problem = draft.Problem;
        _result = draft.Result;
        _title = draft.SuggestedTitle;
        _followUps.AddRange(draft.FollowUps);
        foreach (var step in draft.Steps)
        {
            _steps.Add(new NoteStep(NextId(), step));
        }
    }

    /// <summary>Raised after any mutation. The caller decides what to redraw and when to save.</summary>
    public event Action? Changed;

    /// <summary>
    /// Increments once per accepted mutation. <see cref="AutoSave"/> compares it against what it last
    /// wrote, so a save that finishes while the technician keeps typing does not mark the newer text saved.
    /// </summary>
    public int Revision { get; private set; }

    public string Problem => _problem;

    public string Result => _result;

    public string Title => _title;

    public IReadOnlyList<NoteStep> Steps => _steps;

    public IReadOnlyList<string> FollowUps => _followUps;

    /// <summary>Steps still showing the ⚠ marker — what the pane's footer counts.</summary>
    public int UnverifiedSteps => _steps.Count(step => step.NeedsVerification);

    public void SetProblem(string text) => Set(ref _problem, text);

    public void SetResult(string text) => Set(ref _result, text);

    public void SetTitle(string text) => Set(ref _title, text);

    public void SetStepText(string id, string text)
    {
        if (Find(id) is { } step && step.SetText(text))
        {
            Bump();
        }
    }

    /// <summary>`Alt+C`. No-op on a step that is already confirmed, so it cannot manufacture a save.</summary>
    public void ConfirmStep(string id)
    {
        if (Find(id) is { } step && step.Confirm())
        {
            Bump();
        }
    }

    /// <summary>
    /// The note's follow-ups edited as one block, which is how Spec §5 S3 draws them. Blank lines are
    /// dropped: the schema's array has no place for them and they would come back as empty bullets.
    /// </summary>
    public void SetFollowUpsText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text
            .Split('\n')
            .Select(line => line.TrimEnd('\r').Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.SequenceEqual(_followUps, StringComparer.Ordinal))
        {
            return;
        }

        _followUps.Clear();
        _followUps.AddRange(lines);
        Bump();
    }

    public string FollowUpsText() => string.Join('\n', _followUps);

    /// <summary>`Enter` at the end of a step. Returns the new step so the caller can put the caret in it.</summary>
    public NoteStep InsertStepAfter(string? id)
    {
        var step = new NoteStep(NextId());
        var at = id is null ? _steps.Count : IndexOf(id) + 1;
        _steps.Insert(at < 0 ? _steps.Count : at, step);
        Bump();
        return step;
    }

    /// <summary>
    /// `Backspace` on an empty step. Silent when the id is unknown — the caller is a keystroke, and the
    /// step may be gone between the key going down and the handler running.
    ///
    /// Refuses the last one. A note with no Steps section has no shape, and the technician would be left
    /// holding Backspace at a heading with nothing to type into.
    /// </summary>
    /// <returns>Whether the step was removed.</returns>
    public bool DeleteStep(string id)
    {
        var at = IndexOf(id);
        if (at < 0 || _steps.Count <= 1)
        {
            return false;
        }

        _steps.RemoveAt(at);
        Bump();
        return true;
    }

    /// <summary>
    /// `Alt+↑` / `Alt+↓`, and the drag handle. Returns false at either end so the caller can leave the
    /// focus where it is rather than appearing to have done something.
    /// </summary>
    public bool MoveStep(string id, int delta)
    {
        var at = IndexOf(id);
        if (at < 0 || delta == 0)
        {
            return false;
        }

        var to = at + delta;
        if (to < 0 || to >= _steps.Count)
        {
            return false;
        }

        var step = _steps[at];
        _steps.RemoveAt(at);
        _steps.Insert(to, step);
        Bump();
        return true;
    }

    /// <summary>
    /// What gets written. Steps with no text are left out: the schema requires <c>minLength 1</c>, so a
    /// document containing one could not be saved at all, and refusing the whole save would mean a
    /// technician who pressed Enter and then went to lunch loses every other edit too. The empty step
    /// stays in the editor and is written as soon as it says something.
    /// </summary>
    public DraftNote ToSchema() => _origin with
    {
        Problem = _problem,
        Result = _result,
        SuggestedTitle = _title,
        FollowUps = [.. _followUps],
        Steps = [.. _steps.Where(step => !string.IsNullOrWhiteSpace(step.Text)).Select(step => step.ToSchema())],
    };

    private NoteStep? Find(string id) => _steps.Find(step => step.Id == id);

    private int IndexOf(string id) => _steps.FindIndex(step => step.Id == id);

    private string NextId() => $"s{_nextId++}";

    private void Set(ref string field, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (field == text)
        {
            return;
        }

        field = text;
        Bump();
    }

    private void Bump()
    {
        Revision++;
        Changed?.Invoke();
    }
}
