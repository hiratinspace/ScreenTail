using ScreenTail.Core.History;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Shell;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.History;

/// <summary>
/// A publish that may have reached its ticket and was never confirmed, said outside Review (ST-093,
/// contract v10): History's "Not confirmed" beside the status, the tray's line between sessions, and
/// the count the machine tells for it.
/// </summary>
public sealed class NotConfirmedTests
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };

    [Theory]
    [InlineData("draft_ready", SessionStatus.Draft)]
    [InlineData("draft_failed", SessionStatus.Draft)]
    [InlineData(SessionStateNames.Published, SessionStatus.Published)]
    public void TheMarkIsSaidBesideTheStatusAndDoesNotChangeIt(string state, SessionStatus status)
    {
        // The session is still a draft that may be discarded, or published in part and kept. What may be
        // discarded goes on reading the status, as migration 0013 promised.
        var inDoubt = Summary(state, unconfirmed: true);

        Assert.True(SessionHistory.PublishUnconfirmed(inDoubt));
        Assert.Equal(status, SessionHistory.StatusOf(inDoubt));
        Assert.Equal(status != SessionStatus.Published, SessionHistory.MayDiscard(SessionHistory.StatusOf(inDoubt)));
        Assert.False(SessionHistory.PublishUnconfirmed(Summary(state, unconfirmed: false)));
    }

    [Fact]
    public void ADiscardedSessionNeverSaysNotConfirmed()
    {
        // INV-12: the row keeps the mark, and the technician's discard is still the last word on it.
        var discarded = Summary(SessionStateNames.Discarded, unconfirmed: true);

        Assert.False(SessionHistory.PublishUnconfirmed(discarded));
        Assert.Equal(SessionStatus.Discarded, SessionHistory.StatusOf(discarded));
    }

    [Theory]
    [InlineData("draft", true, true)]
    [InlineData("published", true, true)]
    [InlineData("partial", true, true)]
    [InlineData("draft", false, false)]
    [InlineData("published", false, false)]
    [InlineData("discarded", true, false)]
    [InlineData("Discarded", true, false)]
    public void MarkAsPublishedIsOfferedOnlyForARowInDoubtThatWasNotDiscarded(string status, bool unconfirmed, bool offered)
    {
        var row = new SessionRow { Id = "s1", StartedAt = DateTimeOffset.UnixEpoch, DurationMs = 1, Status = status, Frames = 0, FramesPurged = 0, PublishUnconfirmed = unconfirmed };

        Assert.Equal(offered, SessionHistory.MayMarkPublished(row));
    }

    [Fact]
    public void TheLabelIsItsOwnWordsNotPublishedAndNotFailed()
    {
        Assert.Equal("Not confirmed", SessionHistory.NotConfirmedLabel);
        Assert.DoesNotContain(SessionHistory.NotConfirmedLabel, Enum.GetNames<SessionStatus>(), StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual("Failed", SessionHistory.NotConfirmedLabel, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheNewMembersCrossThePipe()
    {
        // v10: a new command, a new member on a sessions row and one on the snapshot. The framing rejects
        // unknown members, which is why the version moved; here they are known.
        using var stream = new MemoryStream();
        await IpcFraming.WriteAsync<IpcCommand>(stream, new MarkPublishedCommand { RequestId = 4, SessionId = "s1" }, TestContext.Current.CancellationToken);
        await IpcFraming.WriteAsync<IpcEvent>(
            stream,
            new SessionsListed { RequestId = 5, Sessions = [new SessionRow { Id = "s1", StartedAt = DateTimeOffset.UnixEpoch, DurationMs = 1, Status = "draft", Frames = 0, FramesPurged = 0, PublishUnconfirmed = true }] },
            TestContext.Current.CancellationToken);
        await IpcFraming.WriteAsync<IpcEvent>(stream, new StateChanged { State = new CaptureStateSnapshot { State = CaptureStates.Idle, PublishesUnconfirmed = 2 } }, TestContext.Current.CancellationToken);
        var wire = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;

        var mark = Assert.IsType<MarkPublishedCommand>(await IpcFraming.ReadAsync<IpcCommand>(stream, TestContext.Current.CancellationToken));
        var listed = Assert.IsType<SessionsListed>(await IpcFraming.ReadAsync<IpcEvent>(stream, TestContext.Current.CancellationToken));
        var changed = Assert.IsType<StateChanged>(await IpcFraming.ReadAsync<IpcEvent>(stream, TestContext.Current.CancellationToken));

        Assert.Equal("s1", mark.SessionId);
        Assert.True(Assert.Single(listed.Sessions).PublishUnconfirmed);
        Assert.Equal(2, changed.State.PublishesUnconfirmed);
        Assert.Contains("\"type\":\"mark_published\"", wire, StringComparison.Ordinal);
        Assert.Contains("\"publish_unconfirmed\":true", wire, StringComparison.Ordinal);
        Assert.Contains("\"publishes_unconfirmed\":2", wire, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CaptureStates.Idle, 1, "Not capturing · 1 publish not confirmed")]
    [InlineData(CaptureStates.Idle, 3, "Not capturing · 3 publishes not confirmed")]
    [InlineData(CaptureStates.DraftPending, 1, "Not capturing: drafting the note · 1 publish not confirmed")]
    [InlineData(CaptureStates.DraftReady, 2, "Draft ready · 2 publishes not confirmed")]
    [InlineData(CaptureStates.DraftFailed, 1, "Draft failed · 1 publish not confirmed")]
    public void BetweenSessionsTheTraySaysHowManyPublishesAreNotConfirmed(string state, int count, string line)
    {
        var presence = Presence(new CaptureStateSnapshot { State = state, PublishesUnconfirmed = count });

        // The menu's row always says it. The tooltip says it when the whole of it fits in what Windows
        // shows, and otherwise keeps to the state, whole ("drafting the note" is the one that is too long).
        Assert.Equal(line, presence.StateLine);
        var whole = "ScreenTail · " + line;
        var stateOnly = whole[..whole.LastIndexOf(" · ", StringComparison.Ordinal)];
        Assert.Equal(state == CaptureStates.DraftPending ? stateOnly : whole, presence.Tooltip);
        Assert.True(presence.Tooltip.Length <= TrayPresence.TooltipLimit);
    }

    [Theory]
    [InlineData(CaptureStates.Recording)]
    [InlineData(CaptureStates.Paused)]
    [InlineData(CaptureStates.Suppressed)]
    [InlineData(CaptureStates.Finalizing)]
    [InlineData("new_state")]
    public void WhileASessionIsBeingCapturedTheTraySaysOnlyThat(string state)
    {
        // INV-4: the one indicator that is always on screen says what capture is doing and nothing after it.
        var without = Presence(new CaptureStateSnapshot { State = state, SessionId = "s1", RemoteTool = "screenconnect", ElapsedMs = 761_000 });
        var with = Presence(new CaptureStateSnapshot { State = state, SessionId = "s1", RemoteTool = "screenconnect", ElapsedMs = 761_000, PublishesUnconfirmed = 2 });

        Assert.Equal(without, with);
        Assert.DoesNotContain("not confirmed", with.StateLine, StringComparison.Ordinal);
    }

    [Fact]
    public void ATooltipTheCountWouldOverflowKeepsTheStateWholeAndTheMenuStillSaysIt()
    {
        // Windows cuts a tray tooltip at 63 characters, and a state cut short is a state misread.
        var presence = Presence(new CaptureStateSnapshot { State = CaptureStates.DraftReady, SessionId = "s1", RemoteTool = "screenconnect", ElapsedMs = 3_900_000, PublishesUnconfirmed = 12 });

        Assert.Equal("ScreenTail · Draft ready: screenconnect (1:05:00)", presence.Tooltip);
        Assert.Equal("Draft ready: screenconnect (1:05:00) · 12 publishes not confirmed", presence.StateLine);
    }

    [Fact]
    public void NoneNotConfirmedSaysNothingAndAnOfflineTrayDoesNotGuess()
    {
        Assert.Equal("Not capturing", Presence(new CaptureStateSnapshot { State = CaptureStates.Idle }).StateLine);
        Assert.Null(TrayPresence.NotConfirmed(0));

        var offline = TrayPresence.From(new ShellSnapshot(ServiceConnection.Unavailable, new CaptureStateSnapshot { State = CaptureStates.Idle, PublishesUnconfirmed = 2 }, ShellView.Review, null));
        Assert.DoesNotContain("not confirmed", offline.StateLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMachineTellsTheCountWhenAPublishGoesIntoDoubtAndWhenItIsSettled()
    {
        await using var harness = await MachineHarness.StartAsync();
        var machine = harness.Machine;
        var told = new List<CaptureStateSnapshot>();
        var transitions = 0;
        machine.CountsChanged += told.Add;
        machine.StateChanged += _ => transitions++;
        await harness.Store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, Rdp, false, null), TestContext.Current.CancellationToken);

        Assert.True(await harness.Store.SavePublishRecordAsync("s1", new Dictionary<string, string> { ["ticket_note"] = PublishRecord.Unknown }, TestContext.Current.CancellationToken));
        await machine.PublishOutcomesChangedAsync(TestContext.Current.CancellationToken);
        await machine.CountsTelling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Nothing moved, so nothing is told again.
        await machine.PublishOutcomesChangedAsync(TestContext.Current.CancellationToken);
        await machine.CountsTelling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(await harness.Store.SavePublishRecordAsync("s1", new Dictionary<string, string> { ["ticket_note"] = PublishRecord.Landed }, TestContext.Current.CancellationToken));
        await machine.PublishOutcomesChangedAsync(TestContext.Current.CancellationToken);
        await machine.CountsTelling.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([1, 0], told.Select(s => s.PublishesUnconfirmed));
        Assert.Equal(0, machine.Snapshot.PublishesUnconfirmed);

        // A count is not a transition: the guards and the session loop are not woken for it.
        Assert.Equal(0, transitions);
    }

    [Fact]
    public async Task AServiceThatStartsWithAPublishInDoubtSaysSoFromItsFirstSnapshot()
    {
        // The mark is in the store, so it outlives the service; the tray must not wait for the next publish.
        await using var harness = await MachineHarness.StartAsync();
        await harness.Store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, Rdp, false, null), TestContext.Current.CancellationToken);
        await harness.Store.FinalizeSessionAsync("s1", new FinalizeInfo(10_000, false), TestContext.Current.CancellationToken);
        Assert.True(await harness.Store.SavePublishRecordAsync("s1", new Dictionary<string, string> { ["ticket_note"] = PublishRecord.Unknown }, TestContext.Current.CancellationToken));

        _ = await harness.Machine.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Machine.Snapshot.PublishesUnconfirmed);
    }

    private static TrayPresence Presence(CaptureStateSnapshot capture) =>
        TrayPresence.From(new ShellSnapshot(ServiceConnection.Connected, capture, ShellView.Review, null));

    private static SessionSummary Summary(string state, bool unconfirmed) =>
        new("s1", DateTimeOffset.UnixEpoch, 60_000, "rdp", state, false, 0, 0, false, "Printer offline", unconfirmed);
}
