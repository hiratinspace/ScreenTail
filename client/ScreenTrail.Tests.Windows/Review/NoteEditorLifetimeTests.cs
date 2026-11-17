using System.Runtime.Versioning;
using ScreenTrail.Tests.Windows.Detection;
using ScreenTrail.UI.Review;
using static ScreenTrail.Tests.Windows.Review.ReviewSessions;

namespace ScreenTrail.Tests.Windows.Review;

/// <summary>
/// The note editor after the shell has let go of its pane (T-169).
///
/// The shell now disposes a Review pane as it takes it off the screen, and the pane's view is unloaded a
/// moment later, so the view's autosave timer can tick once more against a disposed editor. A note whose
/// last write failed is still due a retry then, and the retry reached the autosave's disposed gate and
/// threw out of the timer's handler.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NoteEditorLifetimeTests
{
    [Fact]
    public async Task ATickAfterTheEditorIsDisposedDoesNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var time = new ManualTime(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
        var writes = 0;
        var model = new NoteEditorViewModel(
            Session(Frames(1)),
            write: (_, _) =>
            {
                writes++;
                throw new IOException("The capture service did not save the note.");
            },
            time: time);
        model.Problem = "Printer offline after the update";

        await model.DisposeAsync();
        Assert.Equal(1, writes);
        time.Advance(TimeSpan.FromSeconds(10));

        await model.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, writes);
    }
}
