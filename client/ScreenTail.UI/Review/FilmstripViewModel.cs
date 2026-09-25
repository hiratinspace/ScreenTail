using System.Collections.ObjectModel;
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

    /// <summary>Spec §5 S3: excluded frames are dimmed, and say so in a word as well (Spec §7).</summary>
    public string StateLabel => Cell.Included ? "included" : "excluded";

    public string? OcrText => Cell.Frame.OcrText;

    public bool HasOcrText => !string.IsNullOrWhiteSpace(Cell.Frame.OcrText);

    /// <summary>Where the cursor was, for the marker in the enlarged view. Null when it was not recorded.</summary>
    public Point? Cursor => Cell.Frame.Cursor;

    internal void Refresh(FrameCell cell)
    {
        Cell = cell;
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(Included));
        OnPropertyChanged(nameof(StateLabel));
    }
}

/// <param name="Cell">The interval itself. <see cref="GapCell.Description"/> is the tooltip.</param>
public sealed class GapItem(GapCell cell) : StripItem
{
    public GapCell Cell { get; } = cell;

    public override long TsMs => Cell.TsMs;

    public string Description => Cell.Description;

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
public sealed partial class FilmstripViewModel : ObservableObject, IDisposable
{
    private readonly Filmstrip _strip;
    private readonly IReviewFrames? _frames;
    private readonly FrameBlur? _blur;
    private readonly UndoWindow<FrameItem> _deletes;

    /// <param name="frames">Where the images come from and where the edits go. In the running
    /// application that is the service, over the pipe; the harness passes nothing and shows no images.</param>
    public FilmstripViewModel(
        Session session,
        IReviewFrames? frames = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _strip = new Filmstrip(session);
        _frames = frames;
        _blur = frames is not null ? new FrameBlur(frames) : null;

        _deletes = new UndoWindow<FrameItem>(
            commit: Committed,
            undo: Restored,
            window: TimeSpan.FromSeconds(5),
            time: time);

        Items = [.. _strip.Cells.Select(Item)];
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
    /// Fills in the thumbnails. Called once the pane is up, so the first paint is not blocked on IO.
    ///
    /// The bytes are decoded small and let go: a 150-frame session is a few tens of megabytes of
    /// thumbnails, not hundreds of megabytes of screenshots nobody is looking at (P2-3).
    /// </summary>
    public async Task LoadImagesAsync(CancellationToken ct = default)
    {
        if (_frames is null)
        {
            return;
        }

        foreach (var item in Items.OfType<FrameItem>())
        {
            if (await _frames.ImageAsync(item.Cell.Frame, ct).ConfigureAwait(true) is { } bytes)
            {
                item.Thumbnail = Thumbnails.Decode(bytes);
            }
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

    /// <summary>`Space`. Persisted immediately: the usual reason to exclude is that it must not go out.</summary>
    [RelayCommand]
    private async Task ToggleIncludeAsync(FrameItem? item)
    {
        if (item is null || _strip.Toggle(item.Id) is not { } included)
        {
            return;
        }

        item.Refresh(_strip.Frames.First(cell => cell.Frame.Id == item.Id));
        OnPropertyChanged(nameof(Header));
        if (_frames is not null)
        {
            await _frames.SetIncludedAsync(item.Id, included).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// `Del`. The frame leaves the strip at once and the toast offers five seconds to take it back; the
    /// row is only deleted when that window closes. Spec §5 S3 asks for exactly this and no confirmation.
    /// </summary>
    [RelayCommand]
    private void Delete(FrameItem? item)
    {
        if (item is null || !Items.Contains(item))
        {
            return;
        }

        if (Enlarged == item)
        {
            Enlarged = null;
            OnPropertyChanged(nameof(IsEnlarged));
        }

        _strip.Remove(item.Id);
        Items.Remove(item);
        Renumber();

        // Staged after the removal, so if this displaces an earlier pending delete that one commits
        // against a strip that already agrees it is gone.
        _deletes.Stage(item);
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(UndoToast));
        OnPropertyChanged(nameof(HasUndoToast));
    }

    [RelayCommand]
    private void Undo()
    {
        if (_deletes.Undo())
        {
            OnPropertyChanged(nameof(UndoToast));
            OnPropertyChanged(nameof(HasUndoToast));
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

        Enlarged = item;
        ShowOcrText = false;
        OnPropertyChanged(nameof(IsEnlarged));
    }

    [RelayCommand]
    private void CloseEnlarged()
    {
        if (Enlarged is { } item)
        {
            item.Image = null;
        }

        Enlarged = null;
        OnPropertyChanged(nameof(IsEnlarged));
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
    }

    private static StripItem Item(FilmstripCell cell) => cell switch
    {
        FrameCell frame => new FrameItem(frame),
        GapCell gap => new GapItem(gap),
        _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null),
    };

    private void Committed(FrameItem item)
    {
        // Fire and forget is wrong here and this is not it: the delete is already reflected everywhere the
        // technician can see, and awaiting inside a timer tick would block the pane. A failure is recorded
        // rather than thrown, because there is no longer a screen to show it on.
        _ = DeleteForGoodAsync(item);
    }

    private async Task DeleteForGoodAsync(FrameItem item)
    {
        if (_frames is null)
        {
            return;
        }

        try
        {
            await _frames.DeleteAsync(item.Id).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            DeleteError = error.Message;
        }
    }

    /// <summary>Set when the permanent delete failed after the window closed. Read by the diagnostics panel.</summary>
    public string? DeleteError { get; private set; }

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
