using System.Runtime.Versioning;
using System.Windows;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.Tests.Windows.Review;
using ScreenTail.UI.History;

namespace ScreenTail.Tests.Windows.History;

/// <summary>
/// History's "Not confirmed" badge and Mark as published (ST-093, contract v10): which rows say it, when
/// the button may be pressed, what is asked for, and what the screen says afterwards.
///
/// The question and the service are a delegate that remembers what it was asked. The flow behind it
/// (asked first, never during a capture) is MarkAsPublishedTests on the Mac, and the service's rule is
/// PublishCommandsTests; that the badge and the button are drawn, named and bound is held from the XAML
/// in NotConfirmedControlsTests.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HistoryNotConfirmedTests
{
    [Fact]
    public void ARowInDoubtSaysSoBesideItsStatusAndToAScreenReader()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var service = new FakeService(Row("s1", "draft", unconfirmed: true), Row("s2", "published", unconfirmed: true), Row("s3", "published"), Row("s4", "discarded", unconfirmed: true));
            var model = service.Model();
            await model.RefreshAsync();

            // The status is still the status: a draft in doubt is a draft, and may still be discarded.
            Assert.Equal(["Draft", "Published", "Published", "Discarded"], model.Rows.Select(r => r.Status));
            Assert.Equal([true, true, false, false], model.Rows.Select(r => r.NotConfirmed));
            Assert.Equal([Visibility.Visible, Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed], model.Rows.Select(r => r.NotConfirmedVisibility));
            Assert.Equal(["Not confirmed", "Not confirmed", string.Empty, string.Empty], model.Rows.Select(r => r.NotConfirmedLabel));

            Assert.StartsWith("Draft, publish not confirmed, started ", model.Rows[0].Spoken, StringComparison.Ordinal);
            Assert.StartsWith("Published, started ", model.Rows[2].Spoken, StringComparison.Ordinal);
            Assert.DoesNotContain("not confirmed", model.Rows[3].Spoken, StringComparison.Ordinal);

            model.Select(model.Rows.Take(1));
            Assert.Equal(1, model.DiscardableCount);
        });
    }

    [Fact]
    public void TheButtonIsForExactlyOneSelectedRowInDoubt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var service = new FakeService(Row("s1", "draft", unconfirmed: true), Row("s2", "published", unconfirmed: true), Row("s3", "published"), Row("s4", "discarded", unconfirmed: true));
            var model = service.Model();
            await model.RefreshAsync();

            Assert.False(model.MarkPublishedCommand.CanExecute(null));

            model.Select([model.Rows[0]]);
            Assert.True(model.MarkPublishedCommand.CanExecute(null));

            // Each is a statement about its own ticket, so two at once is not offered.
            model.Select(model.Rows.Take(2));
            Assert.False(model.MarkPublishedCommand.CanExecute(null));

            model.Select([model.Rows[2]]);
            Assert.False(model.MarkPublishedCommand.CanExecute(null));

            // INV-12: a discarded session is never offered, whatever its row still carries.
            model.Select([model.Rows[3]]);
            Assert.False(model.MarkPublishedCommand.CanExecute(null));
        });
    }

    [Fact]
    public void WithNothingToAskThroughTheButtonIsOff()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var model = new HistoryViewModel(new ShellState(), _ => Task.FromResult<IReadOnlyList<SessionRow>?>([Row("s1", "draft", unconfirmed: true)]));
            await model.RefreshAsync();
            model.Select(model.Rows);

            Assert.False(model.MarkPublishedCommand.CanExecute(null));
        });
    }

    [Fact]
    public void MarkingAsksForThatSessionReadsTheListAgainAndSaysSo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var service = new FakeService(Row("s1", "draft", unconfirmed: true), Row("s2", "draft"));
            var model = service.Model();
            var refocused = 0;
            model.Discarded += (_, _) => refocused++;
            await model.RefreshAsync();
            model.Select([model.Rows[0]]);

            await model.MarkPublishedCommand.ExecuteAsync(null);

            Assert.Equal(["s1"], service.Asked);
            Assert.Equal(2, service.Loads);
            Assert.Equal(["Published", "Draft"], model.Rows.Select(r => r.Status));
            Assert.All(model.Rows, r => Assert.False(r.NotConfirmed));
            Assert.Equal(MarkAsPublished.Done, model.Notice);
            Assert.False(model.DiscardFailed);

            // The button is off again with nothing selected, so focus goes back to the list.
            Assert.Equal(1, refocused);
            Assert.False(model.MarkPublishedCommand.CanExecute(null));
        });
    }

    [Fact]
    public void NotAgreeingChangesNothingAndKeepsTheSelection()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var service = new FakeService(Row("s1", "draft", unconfirmed: true)) { Outcome = new ConfirmedResult(ConfirmedOutcome.Declined) };
            var model = service.Model();
            await model.RefreshAsync();
            model.Select(model.Rows);

            await model.MarkPublishedCommand.ExecuteAsync(null);

            Assert.Equal(1, service.Loads);
            Assert.Single(model.Selected);
            Assert.True(model.Rows[0].NotConfirmed);
            Assert.Equal(string.Empty, model.Notice);
            Assert.False(model.DiscardFailed);
        });
    }

    [Fact]
    public void ARefusalIsShownBesideTheButtonAndTheListIsReadAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            const string refusal = "No publish of this session is waiting to be confirmed, so nothing was changed.";
            var service = new FakeService(Row("s1", "draft", unconfirmed: true)) { Outcome = new ConfirmedResult(ConfirmedOutcome.Refused, refusal) };
            var model = service.Model();
            await model.RefreshAsync();
            model.Select(model.Rows);

            await model.MarkPublishedCommand.ExecuteAsync(null);

            Assert.Equal(refusal, model.DiscardError);
            Assert.True(model.DiscardFailed);
            Assert.Equal(2, service.Loads);
        });
    }

    [Fact]
    public void WhileASessionIsBeingCapturedTheReasonIsSaidAndTheSelectionStays()
    {
        // Spec §4: no modal during a recording. The flow refuses before asking; the list is left alone.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var service = new FakeService(Row("s1", "draft", unconfirmed: true)) { Outcome = new ConfirmedResult(ConfirmedOutcome.Refused, MarkAsPublished.WhileCapturing) };
            var model = service.Model();
            await model.RefreshAsync();
            model.Select(model.Rows);

            await model.MarkPublishedCommand.ExecuteAsync(null);

            Assert.Equal(MarkAsPublished.WhileCapturing, model.DiscardError);
            Assert.Equal(1, service.Loads);
            Assert.Single(model.Selected);
            Assert.True(model.MarkPublishedCommand.CanExecute(null));
        });
    }

    [Fact]
    public void WithNoServiceItSaysSoAndKeepsTheList()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var service = new FakeService(Row("s1", "draft", unconfirmed: true)) { Outcome = new ConfirmedResult(ConfirmedOutcome.NotAvailable, ConfirmedAction.NotConnected) };
            var model = service.Model();
            await model.RefreshAsync();
            model.Select(model.Rows);

            await model.MarkPublishedCommand.ExecuteAsync(null);

            Assert.Equal(ConfirmedAction.NotConnected, model.DiscardError);
            Assert.Equal(1, service.Loads);
            Assert.Single(model.Rows);
        });
    }

    private static SessionRow Row(string id, string status, bool unconfirmed = false) => new()
    {
        Id = id,
        StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(-int.Parse(id[1..], System.Globalization.CultureInfo.InvariantCulture)),
        DurationMs = 60_000,
        Status = status,
        Frames = 1,
        FramesPurged = 0,
        PublishUnconfirmed = unconfirmed,
    };

    /// <summary>The list and the mark, as the service would answer them, remembering what it was asked.</summary>
    private sealed class FakeService(params SessionRow[] rows)
    {
        private List<SessionRow> _rows = [.. rows];

        public int Loads { get; private set; }

        public List<string> Asked { get; } = [];

        /// <summary>How the question and the service end. Done unless a test says otherwise.</summary>
        public ConfirmedResult Outcome { get; init; } = new(ConfirmedOutcome.Done);

        public HistoryViewModel Model() => new(
            new ShellState(),
            _ =>
            {
                Loads++;
                return Task.FromResult<IReadOnlyList<SessionRow>?>(_rows);
            },
            markPublished: (id, _) =>
            {
                Asked.Add(id);
                if (Outcome.Done)
                {
                    _rows = [.. _rows.Select(r => r.Id == id ? r with { Status = "published", PublishUnconfirmed = false } : r)];
                }

                return Task.FromResult(Outcome);
            });
    }
}
