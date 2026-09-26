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
/// screenshot harness. The timeline (ST-076) joined the same day, below the three, and is the one that
/// reaches into another: a transcript line selects its frame in the filmstrip.
/// </summary>
public sealed class ReviewViewModel
{
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
        Note = new NoteEditorViewModel(session, write, confirm);
        Strip = new FilmstripViewModel(session, frames);
        Publish = new PublishViewModel(
            publish ?? PublishPanel.Unconnected(session),
            () => Note.CurrentNote,
            () => Strip.IncludedFrameIds);
        Timeline = new TimelineViewModel(session, id => Strip.Select(id), timelineExpanded, timelineToggled);
    }

    public string SessionId { get; }

    public NoteEditorViewModel Note { get; }

    public FilmstripViewModel Strip { get; }

    public PublishViewModel Publish { get; }

    public TimelineViewModel Timeline { get; }
}
