using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;

namespace ScreenTail.UI.Review;

/// <summary>One thing in the strip, for binding. Either a screenshot or a gap where capture was off.</summary>
public abstract partial class StripItem : ObservableObject
{
    public abstract long TsMs { get; }

    /// <summary>What a screen reader calls the item in the strip (Spec §7). It read the type's name until ST-084.</summary>
    public abstract string SpokenName { get; }
}

public sealed partial class FrameItem : StripItem
{
    internal FrameItem(FrameCell cell)
    {
        _cell = cell;
    }

    [ObservableProperty]
    private FrameCell _cell;

    /// <summary>What the strip draws: the frame decoded at thumbnail size, never the full picture (P2-3).</summary>
    [ObservableProperty]
    private ImageSource? _thumbnail;

    /// <summary>The full image, held only while the frame is enlarged. Null the rest of the time.</summary>
    [ObservableProperty]
    private byte[]? _image;

    public override long TsMs => Cell.Frame.TsMs;

    public string Id => Cell.Frame.Id;

    public string Label => Cell.Label;

    public bool Included => Cell.Included;

    /// <summary>
    /// Spec §5 S3: excluded frames are dimmed, and say so in a word as well (Spec §7). A withheld frame
    /// (it looked sensitive, or capture was off) says why `Space` does nothing to it.
    /// </summary>
    public string StateLabel => Cell.Withheld ? "withheld" : Cell.Included ? "included" : "excluded";

    /// <summary>"frame 2, included": the label and the state, as the strip shows them under the picture.</summary>
    public override string SpokenName => $"{Label}, {StateLabel}";

    /// <summary>
    /// What redaction read on the frame, held only while it is enlarged, like <see cref="Image"/>. The
    /// session crosses the pipe without it (E-20), so it is asked for when the frame is opened.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOcrText))]
    private string? _ocrText;

    public bool HasOcrText => !string.IsNullOrWhiteSpace(OcrText);

    /// <summary>Where the cursor was, for the marker in the enlarged view. Null when it was not recorded.</summary>
    public Point? Cursor => Cell.Frame.Cursor;

    internal void Refresh(FrameCell cell)
    {
        Cell = cell;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Included));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(SpokenName));
    }
}

/// <param name="Cell">The interval itself. <see cref="GapCell.Description"/> is the tooltip.</param>
public sealed class GapItem(GapCell cell) : StripItem
{
    public GapCell Cell { get; } = cell;

    public override long TsMs => Cell.TsMs;

    public string Description => Cell.Description;

    public override string SpokenName => Description;

    /// <summary>"18 s" — drawn on the gap, so the strip says how much is missing without a hover.</summary>
    public string Duration => Cell.DurationMs >= 1000
        ? $"{Cell.DurationMs / 1000} s"
        : $"{Cell.DurationMs} ms";
}

/// <summary>
/// The centre pane (ST-075, Spec §5 S3).
///
/// The strip's contents, the numbering and the two undo windows are <c>ScreenTail.Core.Review</c>'s, and
/// tested there without WPF (ADR-0002). What is here is binding, the image bytes, and the two commands
/// whose whole job is to happen in the right order.
/// </summary>
public sealed partial class FilmstripViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly Filmstrip _strip;
    private readonly IReviewFrames? _frames;
    private readonly FrameBlur? _blur;
    private readonly UndoWindow<FrameItem> _deletes;

    /// <summary>Every frame the strip opened with, in strip order. Never changes, so a thumbnail load can walk it across awaits.</summary>
    private readonly FrameItem[] _frameItems;

    /// <summary>Permanent deletes still waiting on the service, so leaving Review can wait for their answers.</summary>
    private readonly List<Task> _committing = [];

    /// <summary>How far the thumbnail load has walked <see cref="_frameItems"/>; -1 before it starts.</summary>
    private int _thumbnailsAt = -1;

    private CancellationToken _thumbnailToken;

    private readonly TimeProvider _time;
    private readonly TimeSpan _answerWait;

    /// <param name="frames">Where the images come from and where the edits go. In the running
    /// application that is the service, over the pipe; the harness passes nothing and shows no images.</param>
    /// <param name="answerWait">How long leaving Review waits for the service to answer a permanent delete.
    /// Three seconds unless a test says otherwise.</param>
    public FilmstripViewModel(
        Session session,
        IReviewFrames? frames = null,
        TimeProvider? time = null,
        TimeSpan? answerWait = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _strip = new Filmstrip(session);
        _frames = frames;
        _time = time ?? TimeProvider.System;
        _answerWait = answerWait ?? TimeSpan.FromSeconds(3);
        _blur = frames is not null ? new FrameBlur(frames) : null;

        _deletes = new UndoWindow<FrameItem>(
            commit: Committed,
            undo: Restored,
            window: TimeSpan.FromSeconds(5),
            time: time);

        Items = [.. _strip.Cells.Select(Item)];
        _frameItems = [.. Items.OfType<FrameItem>()];
    }

    public ObservableCollection<StripItem> Items { get; }

    /// <summary>What goes out with the note, in strip order: included frames only, deleted ones absent.</summary>
    public IReadOnlyList<string> IncludedFrameIds => [.. _strip.ToPublish().Select(frame => frame.Id)];

    /// <summary>"7 of 14 included" (Spec §5 S3).</summary>
    public string Header => _strip.Header;

    /// <summary>The 5 s toast, or null. Spec §4 prefers this to a dialog in front of every small deletion.</summary>
    public string? UndoToast => _deletes.Pending is { } pending
        ? $"Deleted {pending.Label}"
        : null;

    public bool HasUndoToast => UndoToast is not null;

    [ObservableProperty]
    private FrameItem? _enlarged;

    /// <summary>The strip's selection, two-way with the list, so a transcript line or a note's frame chip can set it (ST-076).</summary>
    [ObservableProperty]
    private StripItem? _selected;

    /// <summary>`T` in the enlarged view: the OCR text the redaction engine read, for verification.</summary>
    [ObservableProperty]
    private bool _showOcrText;

    public bool IsEnlarged => Enlarged is not null;

    /// <summary>
    /// Fills in the thumbnails. Called once the pane is up, so the first paint is not blocked on IO, and
    /// cancelled when the pane goes, so a closed Review stops pulling pictures over the shared pipe (E-105).
    ///
    /// Each one is asked for at thumbnail size (E-30) and decoded small: a 150-frame session is a few
    /// megabytes on the pipe, not every full screenshot nobody is looking at (P2-3). The walk is over the
    /// frames the strip opened with, never the live <see cref="Items"/>, so a Del or an Undo while it
    /// waits cannot pull the list out from under it (T-237). A frame deleted by the time the walk reaches
    /// it is skipped; if it comes back after the walk has passed, the undo fetches its picture.
    /// </summary>
    public async Task LoadImagesAsync(CancellationToken ct = default)
    {
        if (_frames is null)
        {
            return;
        }

        _thumbnailToken = ct;
        for (var at = 0; at < _frameItems.Length; at++)
        {
            ct.ThrowIfCancellationRequested();
            _thumbnailsAt = at;
            var item = _frameItems[at];
            if (item.Thumbnail is null && _strip.Contains(item.Id))
            {
                await LoadThumbnailAsync(_frames, item, ct).ConfigureAwait(true);
            }
        }

        _thumbnailsAt = _frameItems.Length;
    }

    private static async Task LoadThumbnailAsync(IReviewFrames frames, FrameItem item, CancellationToken ct)
    {
        var asked = item.Cell.Frame;
        var bytes = await frames.ThumbnailAsync(asked, ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        // A blur while this was on the pipe has already drawn the thumbnail from the blurred image, and the
        // picture answered here is from before it.
        if (bytes is null || !ReferenceEquals(item.Cell.Frame, asked))
        {
            return;
        }

        try
        {
            item.Thumbnail = Thumbnails.Decode(bytes);
        }
        catch (Exception error) when (error is NotSupportedException or FormatException or IOException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            // Bytes that are not a picture leave this cell empty. They must not take the Loaded handler, and
            // with it the UI process and any delete still in its undo window, down with them.
        }
    }

    /// <summary>
    /// Selects the frame with this id, or clears the selection when there is no such frame — or no id,
    /// which is what a transcript line with no screenshot hands over. Returns whether a frame was selected.
    /// </summary>
    public bool Select(string? frameId)
    {
        Selected = frameId is null ? null : Items.OfType<FrameItem>().FirstOrDefault(item => item.Id == frameId);
        return Selected is not null;
    }

    /// <summary>Call on the pane's timer: closes the undo window once its five seconds have run out.</summary>
    public void Tick()
    {
        var was = _deletes.IsOpen;
        _deletes.Tick();
        if (was != _deletes.IsOpen)
        {
            OnPropertyChanged(nameof(UndoToast));
            OnPropertyChanged(nameof(HasUndoToast));
        }
    }

    /// <summary>
    /// The pane is going away. Anything still inside an undo window becomes permanent now rather than
    /// being abandoned half-done — a frame the technician deleted must not come back because they closed
    /// the window quickly.
    /// </summary>
    public void Dispose() => _deletes.Close();

    /// <summary>
    /// Leaving Review (T-169): as <see cref="Dispose"/>, and then waits for the service's answer to every
    /// permanent delete, so a refusal is recorded in <see cref="EditError"/> before the screen is let go.
    /// Safe to call more than once.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            // Bounded: the pipe may be busy behind a slow publish, and leaving Review must not hang on it.
            // A delete still unanswered then keeps running and records its answer when it comes.
            await Task.WhenAll([.. _committing]).WaitAsync(_answerWait, _time).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
        }
    }

    /// <summary>
    /// Why an edit the technician made did not reach the service, or null (S-35, T-136). Shown under the
    /// strip, because a frame they were told is out or gone may come back the next time the session opens.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditError))]
    private string? _editError;

    public bool HasEditError => EditError is not null;

    /// <summary>`Space`. Persisted immediately: the usual reason to exclude is that it must not go out.</summary>
    [RelayCommand]
    private async Task ToggleIncludeAsync(FrameItem? item)
    {
        // A withheld frame cannot be put in, so there is nothing to change and nothing to tell the service.
        if (item is null || item.Cell.Withheld || _strip.Toggle(item.Id) is not { } included)
        {
            return;
        }

        item.Refresh(_strip.Frames.First(cell => cell.Frame.Id == item.Id));
        OnPropertyChanged(nameof(Header));
        if (_frames is null)
        {
            return;
        }

        var label = item.Label;
        var recorded = await _frames.SetIncludedAsync(item.Id, included).ConfigureAwait(true);

        // An inclusion the service did not record goes back to excluded (ST-078). Filmstrip.Answered decides.
        if (_strip.Answered(item.Id, included, recorded))
        {
            item.Refresh(_strip.Frames.First(cell => cell.Frame.Id == item.Id));
            OnPropertyChanged(nameof(Header));
        }

        // An exclusion the service did not record stays excluded here, so this publish leaves it out. But
        // the store still has it in, and the technician is told so rather than finding it back next time.
        if (recorded)
        {
            EditLanded();
        }
        else if (!included)
        {
            EditError = NotExcluded(label);
        }
    }

    private static string NotExcluded(string label) =>
        $"The service did not record that {label} is excluded. It stays out of this publish, but it may be included again the next time this session is opened.";

    /// <summary>The service is taking edits again, so the last refusal no longer describes the strip.</summary>
    private void EditLanded() => EditError = null;

    /// <summary>
    /// `Del`. The frame leaves the strip at once and the toast offers five seconds to take it back; the
    /// row is only deleted when that window closes. Spec §5 S3 asks for exactly this and no confirmation.
    ///
    /// The exclusion is written now, though (S-35). If the process dies or the pipe drops inside the undo
    /// window the row survives, and it must survive as a frame that does not go out.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeleteAsync(FrameItem? item)
    {
        if (item is null || !Items.Contains(item))
        {
            return;
        }

        if (Enlarged == item)
        {
            Release(item);
            Enlarged = null;
            OnPropertyChanged(nameof(IsEnlarged));
        }

        var wasIncluded = _strip.IsIncluded(item.Id);
        _strip.Remove(item.Id);
        Items.Remove(item);
        Renumber();

        // Staged after the removal, so if this displaces an earlier pending delete that one commits
        // against a strip that already agrees it is gone.
        _deletes.Stage(item);
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(UndoToast));
        OnPropertyChanged(nameof(HasUndoToast));

        // A frame that was already out has nothing to write. A refusal is said as a refused Space is: if the
        // delete does not land either, the frame is back, included, the next time the session opens.
        if (wasIncluded && _frames is not null)
        {
            var label = item.Label;
            if (await _frames.SetIncludedAsync(item.Id, false).ConfigureAwait(true))
            {
                EditLanded();
            }
            else
            {
                EditError = NotExcluded(label);
            }
        }
    }

    /// <summary>
    /// `Ctrl+Z` and the toast's Undo. The frame is back in its place at once; if it went out before the
    /// delete, the exclusion Del wrote is taken back, and the same rule as `Space` applies to the answer.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task UndoAsync()
    {
        if (_deletes.Pending is not { } item || !_deletes.Undo())
        {
            return;
        }

        OnPropertyChanged(nameof(UndoToast));
        OnPropertyChanged(nameof(HasUndoToast));
        if (_frames is null)
        {
            return;
        }

        // Decided before the await: the load below must not see the frame as passed over if it is not.
        var missedByLoad = item.Thumbnail is null && _thumbnailsAt > Array.IndexOf(_frameItems, item);

        if (_strip.IsIncluded(item.Id))
        {
            var recorded = await _frames.SetIncludedAsync(item.Id, true).ConfigureAwait(true);
            if (recorded)
            {
                EditLanded();
            }

            if (_strip.Answered(item.Id, true, recorded)
                && _strip.Frames.FirstOrDefault(cell => cell.Frame.Id == item.Id) is { } cell)
            {
                item.Refresh(cell);
                OnPropertyChanged(nameof(Header));
            }
        }

        // The thumbnail load skipped it while it was gone. If the load had already passed its place it will
        // not come back for it, so it is fetched here.
        if (missedByLoad && !_thumbnailToken.IsCancellationRequested && _strip.Contains(item.Id))
        {
            try
            {
                await LoadThumbnailAsync(_frames, item, _thumbnailToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // The pane closed while it loaded; the next open draws it.
            }
        }
    }

    /// <summary>
    /// `Enter`. `Esc` closes it, and `T` toggles the OCR text over it.
    ///
    /// The full image is fetched here and only here, one frame at a time, and dropped when the view
    /// closes. It is also what a blur is checked against: a frame that never loaded cannot be blurred,
    /// because the technician cannot have seen what they were covering.
    /// </summary>
    [RelayCommand]
    private async Task EnlargeAsync(FrameItem? item)
    {
        if (item is not null && item.Image is null && _frames is not null)
        {
            item.Image = await _frames.ImageAsync(item.Cell.Frame).ConfigureAwait(true);
        }

        if (item is not null && item.OcrText is null)
        {
            item.OcrText = _frames is not null
                ? await _frames.OcrTextAsync(item.Cell.Frame).ConfigureAwait(true)
                : item.Cell.Frame.OcrText;
        }

        // Enter on another frame while one is open moves the view without passing through Esc, so the
        // frame being left lets go of what it held here, after the new one has loaded.
        if (Enlarged is { } previous && previous != item)
        {
            Release(previous);
        }

        Enlarged = item;
        ShowOcrText = false;
        OnPropertyChanged(nameof(IsEnlarged));
    }

    [RelayCommand]
    private void CloseEnlarged()
    {
        if (Enlarged is { } item)
        {
            Release(item);
        }

        Enlarged = null;
        OnPropertyChanged(nameof(IsEnlarged));
    }

    /// <summary>Drops the full image and the OCR text, which a frame holds only while it is the enlarged one.</summary>
    private static void Release(FrameItem item)
    {
        item.Image = null;
        item.OcrText = null;
    }

    [RelayCommand]
    private void ToggleOcrText() => ShowOcrText = !ShowOcrText;

    /// <summary>
    /// `B`, after dragging a rectangle over the enlarged frame. Destructive, and the ordering that makes
    /// it safe belongs to <see cref="FrameBlur"/>, where it is tested without WPF — this only takes what
    /// the store already holds and shows it.
    /// </summary>
    public async Task BlurAsync(FrameItem item, Rect drawn, Size displayed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_blur is null || await _blur.ApplyAsync(item.Cell.Frame, item.Image, drawn, displayed, ct).ConfigureAwait(true) is not { } blurred)
        {
            return;
        }

        item.Image = blurred.Image;
        item.Thumbnail = Thumbnails.Decode(blurred.Image);
        item.Refresh(item.Cell with { Frame = blurred.Frame });
        _strip.Replace(blurred.Frame);

        // A blur takes the frame's OCR text with it: the service cleared it in the same write. The text
        // the enlarged view holds was fetched before the blur, so it goes too, or `T` would still show it.
        item.OcrText = null;

        // The text panel would otherwise stay open over the text the blur has just withdrawn.
        ShowOcrText = false;
    }

    private static StripItem Item(FilmstripCell cell) => cell switch
    {
        FrameCell frame => new FrameItem(frame),
        GapCell gap => new GapItem(gap),
        _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null),
    };

    private void Committed(FrameItem item)
    {
        // Not awaited here: the delete is already reflected everywhere the technician can see, and awaiting
        // inside a timer tick would block the pane. The task is kept, so leaving Review can wait for it,
        // and its answer is read, so a refusal is said rather than lost (T-136).
        _committing.RemoveAll(task => task.IsCompleted);
        _committing.Add(DeleteForGoodAsync(item));
    }

    private async Task DeleteForGoodAsync(FrameItem item)
    {
        if (_frames is null)
        {
            return;
        }

        // The label it had when it was deleted, which is the one the toast showed.
        var label = item.Label;
        bool deleted;
        try
        {
            // False is how the pipe reports a refusal or a service that is not there.
            deleted = await _frames.DeleteAsync(item.Id).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            deleted = false;
        }

        if (deleted)
        {
            EditLanded();
        }
        else
        {
            EditError = $"The service did not confirm that {label} was deleted. Check it the next time this session is opened.";
        }
    }

    private void Restored(FrameItem item)
    {
        _strip.Restore(item.Cell.Frame, item.Cell.Included);

        // Back in time order rather than on the end, so the strip reads the same after an undo as before
        // the delete.
        var at = Items.Count(other => other.TsMs < item.TsMs);
        Items.Insert(at, item);
        Renumber();
        OnPropertyChanged(nameof(Header));
    }

    private void Renumber()
    {
        foreach (var cell in _strip.Frames)
        {
            if (Items.OfType<FrameItem>().FirstOrDefault(item => item.Id == cell.Frame.Id) is { } item)
            {
                item.Refresh(cell);
            }
        }
    }
}
