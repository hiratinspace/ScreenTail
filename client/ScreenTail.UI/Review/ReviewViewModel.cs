using System.ComponentModel;
using ScreenTail.Core.Review;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Review.Publish;
using ScreenTail.UI.Review.Timeline;

namespace ScreenTail.UI.Review;

/// <summary>
/// The Review screen: the note on the left, the screenshots in the middle, publishing on the right
/// (Spec §5 S3; ST-074, ST-075, ST-078).
///
/// Four view models over one session. The first two existed and were never hosted together: until
/// 2026-09-25 the shell's Review area showed the word "Review" and each pane was rendered only by the
/// screenshot harness. The timeline (ST-076) joined the same day, below the three. Two of them reach into
/// the strip: a transcript line and a note's frame chip both select their frame there.
/// </summary>
public sealed class ReviewViewModel : IAsyncDisposable
{
    private bool _disposed;

    /// <param name="frames">Where the images come from and where the edits go — over the pipe in the running application.</param>
    /// <param name="write">Persists the note; null in the harness, which shows a note it cannot save.</param>
    /// <param name="confirm">The typed-confirmation dialog.</param>
    /// <param name="publish">The right pane's rules and its PSA delegates. Null shows the pane with no PSA to connect to.</param>
    /// <param name="timelineExpanded">Whether the bottom panel was open last time (Spec §5 S3: state persists).</param>
    /// <param name="timelineToggled">Told when it opens or closes, so the shell can remember.</param>
    public ReviewViewModel(
        Session session,
        IReviewFrames? frames,
        Func<DraftNote, CancellationToken, Task>? write = null,
        Func<TypedConfirmation, bool>? confirm = null,
        PublishPanel? publish = null,
        bool timelineExpanded = false,
        Action<bool>? timelineToggled = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        SessionId = session.SessionId;
        Strip = new FilmstripViewModel(session, frames);
        Note = new NoteEditorViewModel(session, write, confirm, showFrame: id => Strip.Select(id));

        // These two lambdas are all that keep the note as it arrived, an excluded frame and a frame inside
        // its undo window out of a publish, and the service trusts the list (T-172).
        Publish = new PublishViewModel(
            publish ?? PublishPanel.Unconnected(session),
            () => Note.CurrentNote,
            () => Strip.IncludedFrameIds);
        Timeline = new TimelineViewModel(session, id => Strip.Select(id), timelineExpanded, timelineToggled);

        // The header changes whenever what goes out does, and the publish pane's frame ceiling (E-18)
        // depends on exactly that.
        Strip.PropertyChanged += OnStripChanged;
    }

    public string SessionId { get; }

    public NoteEditorViewModel Note { get; }

    public FilmstripViewModel Strip { get; }

    public PublishViewModel Publish { get; }

    public TimelineViewModel Timeline { get; }

    /// <summary>
    /// Leaving Review (T-169): the last of what was typed is written rather than lost to the autosave's
    /// pause, and a delete still inside its undo window is committed and its answer read. The shell calls
    /// it when it lets go of the pane for good (Core.Shell.HeldPane): another session's pane replaces it,
    /// its session goes away, or the window closes. Safe to call more than once.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Strip.PropertyChanged -= OnStripChanged;
        await Note.DisposeAsync().ConfigureAwait(true);
        await Strip.DisposeAsync().ConfigureAwait(true);
    }

    private void OnStripChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilmstripViewModel.Header))
        {
            Publish.FramesChanged();
        }
    }
}
