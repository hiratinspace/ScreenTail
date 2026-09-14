using ScreenTail.Core.Review;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>ST-074: "Edits persist within 1 s and survive restart", on a clock the test moves by hand.</summary>
public sealed class AutoSaveTests
{
    private readonly ManualTime _clock = new(DateTimeOffset.UnixEpoch);
    private readonly NoteDraft _note = new(Draft());
    private readonly List<string> _written = [];
    private Exception? _throw;

    [Fact]
    public async Task ABurstOfTypingIsOneWrite()
    {
        var save = Save();

        // Forty keystrokes at 50 ms is a sentence, and it must not be forty round trips to an encrypted
        // database sitting under the technician's cursor.
        for (var i = 0; i < 40; i++)
        {
            _note.SetProblem(new string('x', i + 1));
            save.Touch();
            _clock.Advance(TimeSpan.FromMilliseconds(50));
            await save.TickAsync();
        }

        // The ceiling fires during two seconds of continuous typing; the point is that it is not forty.
        Assert.InRange(save.Saves, 1, 3);
    }

    [Fact]
    public async Task NoKeystrokeWaitsLongerThanASecondEvenIfTheTypingNeverStops()
    {
        // A plain debounce never fires at all while somebody types steadily, which is exactly the moment
        // there is most to lose. The ceiling is why the AC's "within 1 s" is true and not aspirational.
        // Measured as the gap between writes over half a minute of unbroken typing, because "it saved
        // eventually" is what a debounce alone would also satisfy.
        var save = Save();
        var writtenAt = new List<TimeSpan>();
        var elapsed = TimeSpan.Zero;

        while (elapsed < TimeSpan.FromSeconds(30))
        {
            _note.SetProblem($"typing {elapsed}");
            save.Touch();
            _clock.Advance(TimeSpan.FromMilliseconds(50));
            elapsed += TimeSpan.FromMilliseconds(50);

            var before = save.Saves;
            await save.TickAsync();
            if (save.Saves > before)
            {
                writtenAt.Add(elapsed);
            }
        }

        Assert.NotEmpty(writtenAt);
        var gaps = writtenAt.Zip(writtenAt.Skip(1), (a, b) => b - a).Prepend(writtenAt[0]);
        Assert.All(gaps, gap => Assert.True(gap <= TimeSpan.FromSeconds(1), $"{gap.TotalMilliseconds} ms went unsaved"));
    }

    [Fact]
    public async Task NothingToSaveIsNoWrite()
    {
        var save = Save();

        save.Touch();
        _clock.Advance(TimeSpan.FromSeconds(5));
        await save.TickAsync();
        await save.FlushAsync();

        Assert.Equal(0, save.Saves);
        Assert.Equal(SaveStatus.Saved, save.Status);
    }

    [Fact]
    public async Task CtrlSDoesNotWaitForTheDebounce()
    {
        var save = Save();
        _note.SetProblem("saved on purpose");
        save.Touch();

        await save.FlushAsync();

        Assert.Equal(["saved on purpose"], _written);
        Assert.Equal(SaveStatus.Saved, save.Status);
    }

    [Fact]
    public async Task TextTypedDuringASaveIsNotCalledSaved()
    {
        // The write is about the document as it was when it started. Marking the newer text saved is how
        // an editor loses a sentence and tells you it did not.
        var gate = new TaskCompletionSource();
        var save = new AutoSave(
            async ct =>
            {
                var text = _note.Problem;
                await gate.Task.WaitAsync(ct);
                _written.Add(text);
            },
            () => _note.Revision,
            _clock);

        _note.SetProblem("first");
        save.Touch();
        var writing = save.FlushAsync();

        _note.SetProblem("typed while saving");
        save.Touch();
        gate.SetResult();
        await writing;

        Assert.Equal(["first"], _written);
        Assert.Equal(SaveStatus.Pending, save.Status);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await save.TickAsync();

        Assert.Equal(["first", "typed while saving"], _written);
        Assert.Equal(SaveStatus.Saved, save.Status);
    }

    [Fact]
    public async Task AFailedWriteSaysSoRatherThanLookingSaved()
    {
        var save = Save();
        _throw = new IOException("the disk went away");
        _note.SetProblem("at risk");
        save.Touch();

        await save.FlushAsync();

        Assert.Equal(SaveStatus.Failed, save.Status);
        Assert.Same(_throw, save.LastError);
        Assert.Empty(_written);
    }

    [Fact]
    public async Task AFailedWriteIsRetriedWithoutAnotherKeystroke()
    {
        // The technician has stopped typing and walked away. Nothing else will happen to prompt a retry,
        // and the edits are still only in memory.
        var save = Save(new AutoSaveOptions { RetryAfter = TimeSpan.FromSeconds(2) });
        _throw = new IOException("the disk went away");
        _note.SetProblem("at risk");
        save.Touch();
        await save.FlushAsync();

        _throw = null;
        _clock.Advance(TimeSpan.FromSeconds(1));
        await save.TickAsync();
        Assert.Equal(SaveStatus.Failed, save.Status);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await save.TickAsync();

        Assert.Equal(["at risk"], _written);
        Assert.Equal(SaveStatus.Saved, save.Status);
        Assert.Null(save.LastError);
    }

    [Fact]
    public async Task ATickIsCheapWhenThereIsNothingToDo()
    {
        // It runs on a UI-thread timer, so the common case has to be a comparison and a return.
        var save = new AutoSave(_ => throw new InvalidOperationException("nothing should be written"), () => _note.Revision, _clock);

        for (var i = 0; i < 100; i++)
        {
            _clock.Advance(TimeSpan.FromMilliseconds(250));
            await save.TickAsync();
        }

        Assert.Equal(SaveStatus.Saved, save.Status);
    }

    private AutoSave Save(AutoSaveOptions? options = null) => new(
        _ =>
        {
            if (_throw is { } error)
            {
                return Task.FromException(error);
            }

            _written.Add(_note.Problem);
            return Task.CompletedTask;
        },
        () => _note.Revision,
        _clock,
        options);
}
