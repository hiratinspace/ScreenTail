using System.Runtime.Versioning;
using ScreenTail.Core.Review;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.Tests.Windows.Review;
using ScreenTail.UI.History;

namespace ScreenTail.Tests.Windows.History;

/// <summary>
/// History's multi-select and bulk discard (Spec §5 S4, v0.4.1 Q6): which selected rows qualify, the
/// typed confirmation of the count, what is sent, and the list after.
///
/// The dialog and the service are delegates that remember what they were asked. The service's half,
/// including that the count is the service's phrase, is SessionDiscardTests and NamedDiscardTests on the
/// Mac; that the table allows a multiple selection and the button is bound is held there from the XAML.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HistoryDiscardTests
{
    [Fact]
    public void ABulkDiscardAsksForTheCountOfWhatItWillDestroyAndRefreshes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var service = new FakeService(Row("s1", "draft"), Row("s2", "partial"), Row("s3", "published"), Row("s4", "discarded"));
            var asked = new List<(TypedConfirmation Confirmation, int Count)>();
            var model = service.Model(confirm: (confirmation, count) =>
            {
                asked.Add((confirmation, count));
                return Task.FromResult(true);
            });
            await model.RefreshAsync();

            model.Select(model.Rows);

            // Four selected, two of them already kept for good: two is what the technician types.
            Assert.Equal(2, model.DiscardableCount);
            Assert.True(model.BulkDiscardCommand.CanExecute(null));
            Assert.Contains("2 of 4", model.SelectionNote, StringComparison.Ordinal);

            await model.BulkDiscardCommand.ExecuteAsync(null);

            Assert.Equal(["s1", "s2"], service.Sent);
            var (typed, count) = Assert.Single(asked);
            Assert.Equal(2, count);
            Assert.True(typed.Accepts("2"));
            Assert.False(typed.Accepts("4"));

            // The list is asked for again and shows what the service now says.
            Assert.Equal(2, service.Loads);
            Assert.Equal(["Discarded", "Discarded", "Published", "Discarded"], model.Rows.Select(r => r.Status));
            Assert.Empty(model.Selected);
            Assert.False(model.BulkDiscardCommand.CanExecute(null));
            Assert.Equal("2 sessions discarded.", model.Notice);
        });
    }

    [Fact]
    public void NothingThatQualifiesMeansNothingToPress()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var service = new FakeService(Row("s1", "published"), Row("s2", "discarded"), Row("s3", "draft"));
            var model = service.Model(confirm: (_, _) => Task.FromResult(true));
            await model.RefreshAsync();

            Assert.False(model.BulkDiscardCommand.CanExecute(null));
            model.Select(model.Rows.Take(2));

            Assert.Equal(0, model.DiscardableCount);
            Assert.False(model.BulkDiscardCommand.CanExecute(null));
        });
    }

    [Fact]
    public void NotTypingTheCountSendsNothingAndKeepsTheSelection()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var service = new FakeService(Row("s1", "draft"), Row("s2", "draft"));
            var model = service.Model(confirm: (_, _) => Task.FromResult(false));
            await model.RefreshAsync();
            model.Select(model.Rows);

            await model.BulkDiscardCommand.ExecuteAsync(null);

            Assert.Null(service.Sent);
            Assert.Equal(1, service.Loads);
            Assert.Equal(2, model.Selected.Count);
        });
    }

    [Fact]
    public void ASelectionThatIncludesTheRunningSessionSaysWhyWithoutADialog()
    {
        // Spec §4: no modal while a session records.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var asked = 0;
            var service = new FakeService(Row("s1", "draft"), Row("live", "pending"));
            var model = service.Model(
                confirm: (_, _) =>
                {
                    asked++;
                    return Task.FromResult(true);
                },
                blocked: ids => ids.Contains("live") ? SessionDiscard.OneStillCapturing : null);
            await model.RefreshAsync();
            model.Select(model.Rows);

            await model.BulkDiscardCommand.ExecuteAsync(null);

            Assert.Equal(0, asked);
            Assert.Null(service.Sent);
            Assert.Equal(SessionDiscard.OneStillCapturing, model.DiscardError);
            Assert.True(model.DiscardFailed);
        });
    }

    [Fact]
    public void ARefusalIsShownAndTheListIsReadAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        UiThread.Run(async () =>
        {
            var service = new FakeService(Row("s1", "draft")) { Refusal = "Nothing was discarded. 1 session no longer exists." };
            var model = service.Model(confirm: (_, _) => Task.FromResult(true));
            await model.RefreshAsync();
            model.Select(model.Rows);

            await model.BulkDiscardCommand.ExecuteAsync(null);

            Assert.Equal("Nothing was discarded. 1 session no longer exists.", model.DiscardError);
            Assert.Equal(2, service.Loads);
        });
    }

    private static SessionRow Row(string id, string status) => new()
    {
        Id = id,
        StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(id.Length),
        DurationMs = 60_000,
        Status = status,
        Frames = 1,
        FramesPurged = 0,
    };

    /// <summary>The list and the discard, as the service would answer them, remembering what it was asked.</summary>
    private sealed class FakeService(params SessionRow[] rows)
    {
        private List<SessionRow> _rows = [.. rows];

        public int Loads { get; private set; }

        public IReadOnlyList<string>? Sent { get; private set; }

        public string? Refusal { get; init; }

        public HistoryViewModel Model(Func<TypedConfirmation, int, Task<bool>> confirm, Func<IReadOnlyCollection<string>, string?>? blocked = null) => new(
            new ShellState(),
            _ =>
            {
                Loads++;
                return Task.FromResult<IReadOnlyList<SessionRow>?>(_rows);
            },
            confirm,
            DiscardAsync,
            blocked);

        /// <summary>Asks the dialog for the count, as ConfirmedAction does with the service's phrase, then discards.</summary>
        private async Task<ConfirmedResult> DiscardAsync(IReadOnlyList<string> ids, Func<string, Task<bool>> typed, CancellationToken ct)
        {
            if (!await typed(ids.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                return new ConfirmedResult(ConfirmedOutcome.Declined);
            }

            Sent = ids;
            if (Refusal is { } refusal)
            {
                return new ConfirmedResult(ConfirmedOutcome.Refused, refusal);
            }

            _rows = [.. _rows.Select(r => ids.Contains(r.Id) ? r with { Status = "discarded" } : r)];
            return new ConfirmedResult(ConfirmedOutcome.Done);
        }
    }
}
