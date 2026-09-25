using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;

namespace ScreenTail.UI.Review;

/// <summary>
/// The Review screen: the note on the left, the screenshots beside it (Spec §5 S3; ST-074, ST-075).
///
/// Two view models that already existed and were never hosted together: until 2026-09-25 the shell's
/// Review area showed the word "Review" and each pane was rendered only by the screenshot harness. This
/// holds them side by side over one session. The timeline (ST-076) has its model in Core and no view
/// yet; it joins this when it does.
/// </summary>
public sealed class ReviewViewModel
{
    /// <param name="frames">Where the images come from and where the edits go — over the pipe in the running application.</param>
    /// <param name="write">Persists the note; null in the harness, which shows a note it cannot save.</param>
    /// <param name="confirm">The typed-confirmation dialog.</param>
    public ReviewViewModel(
        Session session,
        IReviewFrames? frames,
        Func<DraftNote, CancellationToken, Task>? write = null,
        Func<TypedConfirmation, bool>? confirm = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        SessionId = session.SessionId;
        Note = new NoteEditorViewModel(session, write, confirm);
        Strip = new FilmstripViewModel(session, frames);
    }

    public string SessionId { get; }

    public NoteEditorViewModel Note { get; }

    public FilmstripViewModel Strip { get; }
}
