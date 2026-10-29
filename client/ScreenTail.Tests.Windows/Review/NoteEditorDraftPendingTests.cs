using System.Runtime.Versioning;
using ScreenTail.UI.Review;
using static ScreenTail.Tests.Windows.Review.ReviewSessions;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// ST-013 review: Review showed "We couldn't draft this session" and Retry while the session's draft was
/// still queued for the backend. A draft on its way is its own pane, and it offers nothing to retry.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NoteEditorDraftPendingTests
{
    [Fact]
    public async Task ADraftOnItsWayIsNotShownAsFailed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await using var pending = new NoteEditorViewModel(Session(draft: null), draftPending: true);
        await using var failed = new NoteEditorViewModel(Session(draft: null));

        Assert.True(pending.DraftPending);
        Assert.False(pending.DraftFailed);
        Assert.Equal("Drafting this session. The note appears here when it arrives.", pending.DraftPendingMessage);
        Assert.Contains("being drafted", pending.PublishBlockedBecause, StringComparison.Ordinal);

        Assert.False(failed.DraftPending);
        Assert.True(failed.DraftFailed);
    }

    [Fact]
    public async Task OnceTheNoteIsThereBeingQueuedEarlierChangesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        await using var model = new NoteEditorViewModel(Session(Frames(1)), draftPending: true);

        Assert.True(model.HasDraft);
        Assert.False(model.DraftPending);
        Assert.False(model.DraftFailed);
    }
}
