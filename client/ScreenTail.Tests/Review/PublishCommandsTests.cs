using System.Security.Cryptography;
using ScreenTail.Core.Net;
using ScreenTail.Core.Review.Publish;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The service's side of publishing over the pipe (ST-093): the UI names the frames, the service reads
/// their bytes from the store, and the backend's per-destination answer comes back as it is.
/// </summary>
public sealed class PublishCommandsTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FakeGateway _gateway = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private SqliteSessionStore? _store;

    [Fact]
    public async Task ThePublishCarriesTheNamedFramesBytesInOrderAndSkipsOneThatIsGone()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1", "f2"]);

        var reply = await commands.ReplyToAsync(Publish(["f2", "gone", "f1"]));

        var published = Assert.IsType<SessionPublished>(reply);
        Assert.True(published.Results[0].Ok);
        var sent = Assert.Single(_gateway.Published);
        Assert.Equal(["f2", "f1"], sent.Frames.Select(f => f.Id));
        Assert.Equal(Convert.ToBase64String(Bytes("f2")), sent.Frames[0].Image);
        Assert.Equal("48213", sent.TicketId);
        Assert.Equal("Acme Dental", sent.Company);
        Assert.Equal(DateTimeOffset.UnixEpoch, sent.StartedAt);
        Assert.Null(sent.Reviewer);
    }

    [Fact]
    public async Task APublishWaitsForABlurBeingPaintedAndSendsWhatItWrote()
    {
        // ST-084 follow-up review of #257: a publish that read a frame while a blur of it was being painted
        // would send the picture the technician had just asked to cover. The two share the service's turn.
        _ = await CommandsAsync();
        var gate = new Core.Review.FrameEditGate();
        var commands = new PublishCommands(_store!, _gateway, edits: gate);
        await SeedAsync(["f1"]);
        byte[] painted = [0xB1, 0xB2, 0xB3];

        var blurring = await gate.EnterAsync();
        var publishing = commands.ReplyToAsync(Publish(["f1"]));
        await Task.Delay(150);
        Assert.False(publishing.IsCompleted, "the publish read the frames while a blur held the turn");
        Assert.Empty(_gateway.Published);

        await _store!.ApplyUserBlurAsync("f1", painted, new MaskedRegion { X = 0, Y = 0, Width = 10, Height = 10, Kind = MaskKind.UserBlur });
        blurring.Dispose();
        _ = Assert.IsType<SessionPublished>(await publishing);

        Assert.Equal(Convert.ToBase64String(painted), Assert.Single(Assert.Single(_gateway.Published).Frames).Image);
        Assert.False(gate.IsHeld, "the publish kept the turn");
    }

    [Fact]
    public async Task ABlurTakesTheSameTurnAPublishTakes()
    {
        _ = await CommandsAsync();
        var gate = new Core.Review.FrameEditGate();
        var review = new Core.Review.ReviewCommands(_store!, new StubMasker(), gate);
        await SeedAsync(["f1"]);

        var publishing = await gate.EnterAsync();
        var blur = review.ReplyToAsync(new BlurFrameCommand { RequestId = 1, FrameId = "f1", X = 0, Y = 0, Width = 10, Height = 10 });
        await Task.Delay(150);
        Assert.False(blur.IsCompleted, "the blur painted while a publish held the turn");
        publishing.Dispose();
        _ = await blur;
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public async Task ASensitiveFrameIdIsNotSentEvenWhenNamed()
    {
        // session.v1: a sensitive_context frame is never sent or attached. The UI no longer names one, and
        // the service does not take the UI's word for it either.
        var commands = await CommandsAsync();
        await SeedAsync(["f1", "f2"]);
        await AddFrameAsync("s1", "login", 2000, sensitive: true);

        await commands.ReplyToAsync(Publish(["f1", "login", "f2"]));

        Assert.Equal(["f1", "f2"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task AFrameFromAnotherSessionIsNotSentEvenWhenNamed()
    {
        // The ids are global in the store. A frame of some other customer's session must not ride along on
        // this ticket because its id was in the list.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        await _store!.CreateSessionAsync(new NewSession("s2", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await AddFrameAsync("s2", "other", 1000);

        await commands.ReplyToAsync(Publish(["f1", "other"]));

        Assert.Equal(["f1"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task AFrameTheTechnicianExcludedIsNotSentEvenWhenNamed()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1", "f2"]);
        await _store!.SetFrameExcludedAsync("f2", excluded: true);

        await commands.ReplyToAsync(Publish(["f1", "f2"]));

        Assert.Equal(["f1"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task AFrameTakenWhileCaptureWasOffIsNotSentEvenWhenNamed()
    {
        // The same rule the drafting bundle applies (INV-6), so the ticket never carries a frame the model
        // was not allowed to see.
        var commands = await CommandsAsync();
        await _store!.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        await _store.AppendEventAsync("s1", new CaptureStateEvent { TsMs = 2000, State = CaptureState.Suppressed, Reason = CaptureStateReason.PasswordField });
        await _store.AppendEventAsync("s1", new CaptureStateEvent { TsMs = 4000, State = CaptureState.Recording });
        await AddFrameAsync("s1", "before", 1000);
        await AddFrameAsync("s1", "during", 3000);
        await AddFrameAsync("s1", "after", 5000);

        await commands.ReplyToAsync(Publish(["before", "during", "after"]));

        Assert.Equal(["before", "after"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task APendingFrameIsNotSentEvenWhenNamed()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        await _store!.StageFrameAsync("s1", new StagedFrame("raw", 2000, FrameTrigger.Click, 1600, 900, null, new byte[] { 9 }));

        await commands.ReplyToAsync(Publish(["raw", "f1"]));

        Assert.Equal(["f1"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task AFrameNamedTwiceIsAttachedOnce()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1", "f2"]);

        await commands.ReplyToAsync(Publish(["f2", "f1", "f2"]));

        Assert.Equal(["f2", "f1"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task AnEmptyOrMissingIdIsIgnored()
    {
        // The list comes off the pipe, where a null in a string array is valid JSON.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);

        await commands.ReplyToAsync(Publish([null!, string.Empty, "f1"]));

        Assert.Equal(["f1"], Assert.Single(_gateway.Published).Frames.Select(f => f.Id));
    }

    [Fact]
    public async Task APublishWithMoreFramesThanTheBackendTakesIsRefusedBeforeAnyBytesAreRead()
    {
        // E-18. The backend refuses more than 25 frames with a 400, and the client used to find that out
        // after reading, encoding and uploading all of them. The service refuses first, says how many are
        // over, and reads nothing.
        var commands = await CommandsAsync();
        var ids = Enumerable.Range(1, PublishCommands.MaxFrames + 1).Select(i => $"f{i}").ToArray();
        await SeedAsync(ids);

        var command = Publish(ids);
        Assert.Null(await commands.ReplyToAsync(command));
        var result = await commands.HandleAsync(command);

        Assert.False(result!.Ok);
        Assert.Contains($"{PublishCommands.MaxFrames}", result.Error, StringComparison.Ordinal);
        Assert.Contains("26", result.Error, StringComparison.Ordinal);
        Assert.Empty(_gateway.Published);
    }

    [Fact]
    public async Task ExactlyTheCeilingIsSentAndIneligibleNamesDoNotCountAgainstIt()
    {
        // The ceiling is on what would be attached, not on what was typed: an excluded frame or a stray id
        // in the list is dropped before the count, so a strip showing 25 included publishes.
        var commands = await CommandsAsync();
        var ids = Enumerable.Range(1, PublishCommands.MaxFrames).Select(i => $"f{i}").ToArray();
        await SeedAsync(ids);
        await AddFrameAsync("s1", "out", 2000);
        await _store!.SetFrameExcludedAsync("out", excluded: true);

        var reply = await commands.ReplyToAsync(Publish([.. ids, "out", "gone", "f1"]));

        Assert.IsType<SessionPublished>(reply);
        Assert.Equal(PublishCommands.MaxFrames, Assert.Single(_gateway.Published).Frames.Count);
    }

    [Fact]
    public void ThePublishCeilingIsTheBundlesCeiling()
    {
        // One number: the backend's PublishLimits.MaxFrames is BundleLimits.MaxFrames, which is the
        // client's BundleOptions.MaxFrames. A drift here would make the service refuse what the backend
        // takes, or the reverse.
        Assert.Equal(PublishCommands.MaxFrames, new ScreenTail.Core.Intel.BundleOptions().MaxFrames);
    }

    [Fact]
    public async Task ASessionCountsAsPublishedOnlyWhenSomethingLanded()
    {
        // T-128 (ST-098, ST-067). The metric and the style signals are about a note that reached the PSA.
        // Every destination failing is not that, and reporting it would count a session as published that
        // the technician is about to retry.
        var metrics = new RecordingBookkeeping();
        var commands = await CommandsAsync(metrics);
        await SeedAsync(["f1"]);
        _gateway.Outcomes = destinations => [.. destinations.Select(d => new PublishOutcomeRow(d, false, Error: "PSA down"))];

        var nothing = Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));
        Assert.All(nothing.Results, r => Assert.False(r.Ok));
        Assert.Empty(metrics.Reported);
        Assert.Empty(metrics.Learned);

        _gateway.Outcomes = destinations => [.. destinations.Select(d => new PublishOutcomeRow(d, d == "ticket_note", d == "ticket_note" ? "1" : null, Error: d == "ticket_note" ? null : "PSA down"))];

        var partly = Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));
        Assert.Contains(partly.Results, r => r.Ok);
        Assert.Equal([("s1", true)], metrics.Reported);
        Assert.Equal(["s1"], metrics.Learned);
    }

    [Fact]
    public async Task AStoreFaultAfterALandedPublishStillReturnsTheResults()
    {
        // S-37. The note is on the ticket by the time the bookkeeping runs. A metrics or style store that
        // throws then must not turn into a dropped pipe and a result list that says every destination
        // failed, which is what the technician would read as "publish again".
        var metrics = new RecordingBookkeeping { Throws = true };
        using var lines = new StringWriter();
        using var logs = new ScreenTail.Shared.Logging.ScrubbingLoggerProvider(lines, Microsoft.Extensions.Logging.LogLevel.Information);
        var commands = await CommandsAsync(metrics, logs.CreateLogger("publish"));
        await SeedAsync(["f1"]);

        var reply = await commands.ReplyToAsync(Publish(["f1"]));

        var published = Assert.IsType<SessionPublished>(reply);
        Assert.Equal(2, published.Results.Count);
        Assert.All(published.Results, r => Assert.True(r.Ok));
        Assert.Equal([("s1", true)], metrics.Reported);
        Assert.Equal(["s1"], metrics.Learned);
        Assert.Equal(nameof(InvalidOperationException), commands.LastBookkeepingError);

        // Each fault is a line in the service's log with its kind and nothing it carried: not the
        // exception's words, which a store may fill with what it held, and not the session.
        var logged = lines.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, logged.Length);
        Assert.All(logged, line => Assert.Contains(nameof(InvalidOperationException), line, StringComparison.Ordinal));
        Assert.Contains(logged, line => line.Contains("metrics", StringComparison.Ordinal));
        Assert.Contains(logged, line => line.Contains("style", StringComparison.Ordinal));
        Assert.DoesNotContain("locked", lines.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s1", lines.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionAlreadyPublishedIsRefusedUnlessThePaneSaysItIsPublishingAgain()
    {
        // The pane's own guard lives in one window. A pipe that dropped after the first publish landed, a
        // restarted UI, or a second Review window all arrive here with a session already marked
        // published, and a second note on the customer's ticket at the end of a plain resend.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));
        Assert.Single(_gateway.Published);

        var again = Publish(["f1"]) with { RequestId = 8 };
        Assert.Null(await commands.ReplyToAsync(again));
        var refused = await commands.HandleAsync(again);

        Assert.False(refused!.Ok);
        Assert.Equal(PublishCommands.AlreadyPublished, refused.Error);
        Assert.Single(_gateway.Published);

        var confirmed = Publish(["f1"]) with { RequestId = 9, PublishAgain = true };
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(confirmed));
        Assert.Equal(2, _gateway.Published.Count);
    }

    [Fact]
    public async Task ASessionWhosePublishFailedEverywhereIsStillPublishedWithoutTheFlag()
    {
        // Nothing landed, so the session was not marked published and a plain resend duplicates nothing.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Outcomes = destinations => [.. destinations.Select(d => new PublishOutcomeRow(d, false, Error: "PSA down"))];
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));
        _gateway.Outcomes = null;

        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"]) with { RequestId = 8 }));
        Assert.Equal(2, _gateway.Published.Count);
    }

    [Fact]
    public async Task ASecondPublishWhileTheFirstIsStillOnItsWayIsRefusedEvenWhenConfirmed()
    {
        // The pipe dropped mid-publish and the technician, reconnected, looked at the ticket before the
        // first call had landed: nothing there yet, so they confirm. The first is still in flight here.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gateway.Hold = hold.Task;

        var first = commands.ReplyToAsync(Publish(["f1"]));
        await _gateway.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var second = Publish(["f1"]) with { RequestId = 8, PublishAgain = true };
        // Bounded, so a second publish that waits behind the first fails the test instead of hanging it.
        var secondReply = commands.ReplyToAsync(second);
        var finished = await Task.WhenAny(secondReply, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        hold.SetResult();
        Assert.Same(secondReply, finished);
        Assert.Null(await secondReply);
        var refused = await commands.HandleAsync(second);

        Assert.IsType<SessionPublished>(await first);
        Assert.False(refused!.Ok);
        Assert.Equal(PublishCommands.StillPublishing, refused.Error);
        Assert.Single(_gateway.Published);
    }

    [Fact]
    public async Task APublishWhoseGatewayThrewDoesNotHoldTheNextOneAsStillPublishing()
    {
        // Review of #265 (m3). The in-flight mark is taken before the backend is asked; a gateway that
        // throws must not leave it behind, or every later publish of the session is refused for good.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Throws = new HttpRequestException("the backend went away");

        await Assert.ThrowsAsync<HttpRequestException>(() => commands.ReplyToAsync(Publish(["f1"])));
        _gateway.Throws = null;

        // It may have left, so a plain resend is refused as unconfirmed (O2), never as still publishing,
        // and a confirmed one goes.
        await AssertUnconfirmedNotStillPublishingAsync(commands);
    }

    [Fact]
    public async Task ACancelledPublishDoesNotHoldTheNextOneAsStillPublishing()
    {
        // The same for a publish whose connection was closed while the backend was being asked.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Hold = Task.Delay(Timeout.Infinite, TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();

        var first = commands.ReplyToAsync(Publish(["f1"]), cancel.Token);
        await _gateway.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        _gateway.Hold = null;

        await AssertUnconfirmedNotStillPublishingAsync(commands);
    }

    [Fact]
    public async Task TwoConnectionsUsingTheSameRequestIdEachGetTheirOwnRefusal()
    {
        // Review of #265 (m6). Every client numbers its requests from 1, so a second window, or the UI
        // reconnected after a drop, reuses ids the first one is still using. Keyed by id alone, the
        // second refusal overwrote the first and each connection was told the other's reason.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var gone = Publish(["f1"]) with { SessionId = "nope" };
        var refused = Publish(["f1"]);
        Assert.Equal(gone.RequestId, refused.RequestId);

        Assert.Null(await commands.ReplyToAsync(gone, first));
        _gateway.Refusal = "Connect a PSA to publish.";
        Assert.Null(await commands.ReplyToAsync(refused, second));

        var toFirst = await commands.HandleAsync(gone, first);
        var toSecond = await commands.HandleAsync(refused, second);

        Assert.Equal("The session is gone, so there is nothing to publish.", toFirst!.Error);
        Assert.Equal("Connect a PSA to publish.", toSecond!.Error);
    }

    // O2 (owner, review of #265): "may have landed" is remembered by the service, in the store, so a
    // reopened Review, a second window or a restarted service cannot publish over it without the pane
    // saying the resend is meant. Not a session state: History, discard and retention do not see it.

    [Fact]
    public async Task APublishThatMayHaveLandedIsRememberedAndRefusesAPlainResend()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Outcomes = destinations => [.. destinations.Select(d => new PublishOutcomeRow(d, false, Error: "may have been saved", Kind: "unknown"))];

        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));

        var record = await _store!.GetPublishRecordAsync("s1");
        Assert.NotNull(record!.UnconfirmedAt);
        Assert.Equal(new Dictionary<string, string> { ["ticket_note"] = "unknown", ["time_entry"] = "unknown" }, record.Outcomes);
        Assert.NotEqual(Core.Sessions.SessionStateNames.Published, (await _store.GetSessionSummaryAsync("s1"))!.State);

        var again = Publish(["f1"]) with { RequestId = 8 };
        Assert.Null(await commands.ReplyToAsync(again));
        Assert.Equal(PublishCommands.Unconfirmed, (await commands.HandleAsync(again))!.Error);
        Assert.Single(_gateway.Published);

        _gateway.Outcomes = null;
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"]) with { RequestId = 9, PublishAgain = true }));
        Assert.Equal(2, _gateway.Published.Count);
    }

    [Fact]
    public async Task TheMarkSurvivesAServiceRestart()
    {
        // The attempt, then the service restarts (a new store on the same file), then Review reopens.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Outcomes = destinations => [.. destinations.Select(d => new PublishOutcomeRow(d, d == "ticket_note", Error: d == "ticket_note" ? null : "timed out", Kind: d == "ticket_note" ? null : "timed_out"))];
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));
        await _store!.DisposeAsync();
        _gateway.Outcomes = null;

        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(_key));
        var restarted = new PublishCommands(_store, _gateway);

        var record = await _store.GetPublishRecordAsync("s1");
        Assert.NotNull(record!.UnconfirmedAt);
        Assert.Equal(new Dictionary<string, string> { ["ticket_note"] = "landed", ["time_entry"] = "unknown" }, record.Outcomes);
        var again = Publish(["f1"]) with { RequestId = 8 };
        Assert.Null(await restarted.ReplyToAsync(again));
        Assert.False((await restarted.HandleAsync(again))!.Ok);
        Assert.Single(_gateway.Published);
    }

    [Fact]
    public async Task APublishThatEndsCleanClearsTheMark()
    {
        // Landed, or plainly failed after the technician checked: either way nothing is in doubt now.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Outcomes = destinations => [.. destinations.Select(d => new PublishOutcomeRow(d, false, Error: "may have been saved", Kind: "unknown"))];
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));

        _gateway.Outcomes = destinations => [.. destinations.Select(d => d == "ticket_note" ? new PublishOutcomeRow(d, true, "1") : new PublishOutcomeRow(d, false, Error: "ConnectWise refused the member", Kind: "forbidden"))];
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"]) with { RequestId = 8, PublishAgain = true }));

        var record = await _store!.GetPublishRecordAsync("s1");
        Assert.Null(record!.UnconfirmedAt);
        Assert.Equal(new Dictionary<string, string> { ["ticket_note"] = "landed" }, record.Outcomes);
    }

    [Fact]
    public async Task APublishThatThrowsAfterItWentOutLeavesTheSessionUnconfirmed()
    {
        // Written before the backend is asked, so a fault, a cancelled connection or a service that died
        // mid-publish leaves "may have landed" behind rather than nothing.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Throws = new InvalidOperationException("the gateway broke");

        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.ReplyToAsync(Publish(["f1"])));

        var record = await _store!.GetPublishRecordAsync("s1");
        Assert.NotNull(record!.UnconfirmedAt);
        Assert.Equal(new Dictionary<string, string> { ["ticket_note"] = "unknown", ["time_entry"] = "unknown" }, record.Outcomes);
    }

    [Fact]
    public async Task APlainRefusalLeavesNoMark()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Refusal = "This device is not enrolled yet, so nothing can be published.";

        Assert.Null(await commands.ReplyToAsync(Publish(["f1"])));

        var record = await _store!.GetPublishRecordAsync("s1");
        Assert.True(record is null || (record.UnconfirmedAt is null && record.Outcomes.Count == 0));
        _gateway.Refusal = null;
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"]) with { RequestId = 8 }));
    }

    // MJ1 (re-review of #265): the check is per chosen destination, against what the store holds for
    // it, because publish_again only says the technician looked at what the pane knew of.

    [Fact]
    public async Task ACrashBetweenTheOutcomesAndThePublishedMarkStillRefusesAPlainResend()
    {
        // The outcomes were saved and the service died before it marked the session published: the store
        // says the note landed, the session state does not.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        Assert.True(await _store!.SavePublishRecordAsync("s1", new Dictionary<string, string> { ["ticket_note"] = "landed" }));
        Assert.NotEqual(Core.Sessions.SessionStateNames.Published, (await _store.GetSessionSummaryAsync("s1"))!.State);

        var plain = Publish(["f1"]) with { RequestId = 8 };
        Assert.Null(await commands.ReplyToAsync(plain));
        Assert.Equal(PublishCommands.AlreadyPublished, (await commands.HandleAsync(plain))!.Error);
        Assert.Empty(_gateway.Published);
    }

    [Fact]
    public async Task AStoredUnknownRefusesAPlainSendOfThatDestinationEvenWhenAnotherLanded()
    {
        // The note landed and the time entry may have. A pane that only knew of the note sends the time
        // entry plainly; the session is published, but what is in doubt is the time entry.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Outcomes = destinations => [.. destinations.Select(d => d == "ticket_note" ? new PublishOutcomeRow(d, true, "1") : new PublishOutcomeRow(d, false, Error: "timed out", Kind: "timed_out"))];
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));
        _gateway.Outcomes = null;

        var timeEntry = Publish(["f1"]) with { RequestId = 8, Destinations = ["time_entry"] };
        Assert.Null(await commands.ReplyToAsync(timeEntry));
        Assert.Equal(PublishCommands.Unconfirmed, (await commands.HandleAsync(timeEntry))!.Error);
        Assert.Single(_gateway.Published);
    }

    [Fact]
    public async Task ADestinationThatPlainlyFailedGoesWithoutTheFlagWhenAnotherLanded()
    {
        // ST-094 AC3 on reopen: the note landed, the time entry plainly failed. Sending the time entry
        // alone duplicates nothing, so it needs nobody's word.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Outcomes = destinations => [.. destinations.Select(d => d == "ticket_note" ? new PublishOutcomeRow(d, true, "1") : new PublishOutcomeRow(d, false, Error: "ConnectWise refused the member", Kind: "forbidden"))];
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));
        _gateway.Outcomes = null;

        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"]) with { RequestId = 8, Destinations = ["time_entry"] }));
        Assert.Equal(2, _gateway.Published.Count);

        var note = Publish(["f1"]) with { RequestId = 9, Destinations = ["ticket_note"] };
        Assert.Null(await commands.ReplyToAsync(note));
        Assert.Equal(PublishCommands.AlreadyPublished, (await commands.HandleAsync(note))!.Error);
        Assert.Equal(2, _gateway.Published.Count);
    }

    [Fact]
    public async Task ASessionPublishedBeforeTheRecordExistedIsRefusedForEveryDestination()
    {
        // A session published before 0013 has the state and no outcomes: nothing says which destination
        // landed, so all of them may have.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        Assert.True(await _store!.MarkPublishedAsync("s1"));

        var plain = Publish(["f1"]) with { RequestId = 8, Destinations = ["kb_article"] };
        Assert.Null(await commands.ReplyToAsync(plain));
        Assert.Equal(PublishCommands.AlreadyPublished, (await commands.HandleAsync(plain))!.Error);
        Assert.Empty(_gateway.Published);
    }

    [Fact]
    public async Task ALegacyPublishedSessionKeepsItsDoubtAfterOneNewPublish()
    {
        // minor-1 (re-review at bde6a99): the fallback for a session published before 0013 lived only
        // while the record was empty. One confirmed publish of the article wrote a record without the note
        // or the time entry, and a plain send of the note then went. Every destination starts as unknown.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        Assert.True(await _store!.MarkPublishedAsync("s1"));

        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"]) with { Destinations = ["kb_article"], PublishAgain = true }));
        var record = await _store.GetPublishRecordAsync("s1");
        Assert.Equal(new Dictionary<string, string> { ["kb_article"] = "landed", ["ticket_note"] = "unknown", ["time_entry"] = "unknown" }, record!.Outcomes);

        var note = Publish(["f1"]) with { RequestId = 8, Destinations = ["ticket_note"] };
        Assert.Null(await commands.ReplyToAsync(note));
        Assert.False((await commands.HandleAsync(note))!.Ok);
        Assert.Single(_gateway.Published);
    }

    [Fact]
    public async Task ACrashBeforeThePublishedMarkIsMendedAtTheNextPublish()
    {
        // minor-2: the outcomes say something landed and the session is still draft_ready, so the tray
        // and History keep offering it as a draft. The next publish of it marks it published, refused or not.
        var told = new List<string>();
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(_key));
        var commands = new PublishCommands(_store, _gateway, published: (id, _) => { told.Add(id); return Task.FromResult(true); });
        await SeedAsync(["f1"]);
        Assert.True(await _store.SavePublishRecordAsync("s1", new Dictionary<string, string> { ["ticket_note"] = "landed" }));

        var plain = Publish(["f1"]) with { RequestId = 8 };
        Assert.Null(await commands.ReplyToAsync(plain));
        Assert.Equal(PublishCommands.AlreadyPublished, (await commands.HandleAsync(plain))!.Error);

        Assert.Equal(Core.Sessions.SessionStateNames.Published, (await _store.GetSessionSummaryAsync("s1"))!.State);
        Assert.Equal(["s1"], told);
        Assert.Empty(_gateway.Published);
    }

    [Fact]
    public async Task ADestinationItDoesNotKnowIsRefusedBeforeAnythingIsWritten()
    {
        // m-b: a name outside the three would be written ahead as unknown and then stay so for good.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);

        var odd = Publish(["f1"]) with { Destinations = ["ticket_note", "fax"] };
        Assert.Null(await commands.ReplyToAsync(odd));
        Assert.Equal(PublishCommands.UnknownDestination, (await commands.HandleAsync(odd))!.Error);

        Assert.Empty(_gateway.Published);
        var record = await _store!.GetPublishRecordAsync("s1");
        Assert.True(record is null || record.Outcomes.Count == 0);
    }

    [Fact]
    public async Task APlainRefusalAfterAnEarlierUnknownKeepsIt()
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        Assert.True(await _store!.SavePublishRecordAsync("s1", new Dictionary<string, string> { ["ticket_note"] = "unknown", ["time_entry"] = "unknown" }));
        _gateway.Refusal = "This device is not enrolled yet, so nothing can be published.";

        Assert.Null(await commands.ReplyToAsync(Publish(["f1"]) with { PublishAgain = true }));

        var record = await _store.GetPublishRecordAsync("s1");
        Assert.NotNull(record!.UnconfirmedAt);
        Assert.Equal(new Dictionary<string, string> { ["ticket_note"] = "unknown", ["time_entry"] = "unknown" }, record.Outcomes);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("timed_out")]
    public async Task LandedStaysLandedWhenALaterSendOfItIsInDoubt(string kind)
    {
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        Assert.True(await _store!.SavePublishRecordAsync("s1", new Dictionary<string, string> { ["ticket_note"] = "landed" }));
        _gateway.Outcomes = destinations => [.. destinations.Select(d => new PublishOutcomeRow(d, false, Error: "may have been saved", Kind: kind))];

        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"]) with { PublishAgain = true }));

        var record = await _store.GetPublishRecordAsync("s1");
        Assert.Equal(new Dictionary<string, string> { ["ticket_note"] = "landed", ["time_entry"] = "unknown" }, record!.Outcomes);
    }

    [Fact]
    public async Task AnUnconfirmedSessionCanStillBeDiscarded()
    {
        // INV-12: discard is the technician's last word, and a note on the ticket is out of our hands.
        var commands = await CommandsAsync();
        await SeedAsync(["f1"]);
        _gateway.Outcomes = destinations => [.. destinations.Select(d => new PublishOutcomeRow(d, false, Error: "may have been saved", Kind: "unknown"))];
        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"])));

        Assert.Equal(1, await _store!.DiscardSessionsAsync(["s1"]));
    }

    [Fact]
    public async Task ABackendRefusalIsAFailedResultWithItsWords()
    {
        var commands = await CommandsAsync();
        await SeedAsync([]);
        _gateway.Refusal = "Connect a PSA to publish.";

        var command = Publish([]);
        Assert.Null(await commands.ReplyToAsync(command));
        var result = await commands.HandleAsync(command);

        Assert.False(result!.Ok);
        Assert.Equal("Connect a PSA to publish.", result.Error);
    }

    [Fact]
    public async Task ASessionThatIsGoneCannotBePublished()
    {
        var commands = await CommandsAsync();

        var command = Publish([]) with { SessionId = "nope" };
        Assert.Null(await commands.ReplyToAsync(command));
        var result = await commands.HandleAsync(command);

        Assert.False(result!.Ok);
        Assert.Empty(_gateway.Published);
    }

    [Fact]
    public async Task SearchAndIntegrationsPassThrough()
    {
        var commands = await CommandsAsync();
        _gateway.Tickets = [new TicketRow("48213", "Printer offline", "Acme Dental", "New")];
        _gateway.Integrations = [new IntegrationInfo("connectwise", "https://na", "••••1234")];

        var tickets = Assert.IsType<TicketsFound>(await commands.ReplyToAsync(new SearchTicketsCommand { RequestId = 1, Query = "printer" }));
        var integrations = Assert.IsType<IntegrationsListed>(await commands.ReplyToAsync(new GetIntegrationsCommand { RequestId = 2 }));

        Assert.Equal("48213", Assert.Single(tickets.Tickets).Id);
        Assert.Equal("connectwise", Assert.Single(integrations.Integrations).Provider);
        Assert.Equal("printer", _gateway.LastQuery);
    }

    [Fact]
    public async Task TheMappingQuestionsPassThrough()
    {
        var commands = await CommandsAsync();

        var listed = Assert.IsType<CompanyMappingsListed>(await commands.ReplyToAsync(new GetCompanyMappingsCommand { RequestId = 3 }));
        var mapped = await commands.HandleAsync(new MapCompanyCommand { RequestId = 4, PsaCompany = "Acme Dental", DocCompanyId = "7" });

        Assert.Equal("Acme Dental", Assert.Single(listed.Companies).Name);
        Assert.True(mapped!.Ok);
    }

    [Fact]
    public async Task TheSettingsQuestionsAndActionsPassThrough()
    {
        // ST-082: the Settings screen manages integrations through the same service and gateway.
        var commands = await CommandsAsync();

        var listed = Assert.IsType<IntegrationDetailsListed>(await commands.ReplyToAsync(new ListIntegrationsCommand { RequestId = 5 }));
        var checkedEvent = Assert.IsType<IntegrationChecked>(await commands.ReplyToAsync(new CheckIntegrationCommand { RequestId = 6, Provider = "connectwise" }));
        var stored = await commands.HandleAsync(new StoreIntegrationCommand { RequestId = 7, Provider = "hudu", SiteUrl = "https://acme.huducloud.com", Secret = "key" });
        var removed = await commands.HandleAsync(new RemoveIntegrationCommand { RequestId = 8, Provider = "hudu" });
        var unmapped = await commands.HandleAsync(new UnmapCompanyCommand { RequestId = 9, PsaCompany = "Acme Dental" });

        Assert.Equal("connectwise", Assert.Single(listed.Integrations).Provider);
        Assert.True(checkedEvent.Ok);
        Assert.True(stored!.Ok);
        Assert.True(removed!.Ok);
        Assert.True(unmapped!.Ok);
        Assert.Equal(("hudu", "https://acme.huducloud.com", "key"), _gateway.Stored);
    }

    [Fact]
    public async Task CommandsItDoesNotOwnAreLeftAlone()
    {
        var commands = await CommandsAsync();

        Assert.Null(await commands.ReplyToAsync(new StartCommand { RequestId = 1 }));
        Assert.Null(await commands.HandleAsync(new StartCommand { RequestId = 1 }));
    }

    private async Task AssertUnconfirmedNotStillPublishingAsync(PublishCommands commands)
    {
        var plain = Publish(["f1"]) with { RequestId = 8 };
        Assert.Null(await commands.ReplyToAsync(plain));
        Assert.Equal(PublishCommands.Unconfirmed, (await commands.HandleAsync(plain))!.Error);

        Assert.IsType<SessionPublished>(await commands.ReplyToAsync(Publish(["f1"]) with { RequestId = 9, PublishAgain = true }));
        Assert.Single(_gateway.Published);
    }

    private static PublishSessionCommand Publish(string[] frameIds) => new()
    {
        RequestId = 7,
        SessionId = "s1",
        TicketId = "48213",
        TicketCompany = "Acme Dental",
        NoteType = "internal",
        Minutes = 30,
        Destinations = ["ticket_note", "time_entry"],
        Note = Draft(),
        FrameIds = frameIds,
    };

    private async Task<PublishCommands> CommandsAsync(RecordingBookkeeping? bookkeeping = null, Microsoft.Extensions.Logging.ILogger? log = null)
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(_key));
        return bookkeeping is null
            ? new PublishCommands(_store, _gateway)
            : new PublishCommands(_store, _gateway, bookkeeping, bookkeeping, log);
    }

    /// <summary>Paints nothing; the blur's outcome is not what the turn test is about.</summary>
    private sealed class StubMasker : Core.Privacy.IFrameMasker
    {
        public Core.Privacy.MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge) => new(image.ToArray(), 1, 1);
    }

    /// <summary>The metrics reporter and the style learner, remembering what they were told; optionally failing like a store that is down.</summary>
    private sealed class RecordingBookkeeping : IMetricsReporter, ScreenTail.Core.Intel.IStyleLearner
    {
        public bool Throws { get; init; }

        public List<(string SessionId, bool Published)> Reported { get; } = [];

        public List<string> Learned { get; } = [];

        public Task ReportAsync(string sessionId, bool published, CancellationToken ct = default)
        {
            Reported.Add((sessionId, published));
            return Throws ? Task.FromException(new InvalidOperationException("the metrics store is locked")) : Task.CompletedTask;
        }

        public Task LearnAsync(string sessionId, CancellationToken ct = default)
        {
            Learned.Add(sessionId);
            return Throws ? Task.FromException(new InvalidOperationException("the style store is locked")) : Task.CompletedTask;
        }
    }

    private async Task SeedAsync(string[] frames)
    {
        await _store!.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        foreach (var id in frames)
        {
            await _store.SaveRedactedFrameAsync("s1", new StagedFrame(id, 1000, FrameTrigger.Click, 1600, 900, null, new byte[] { 1 }), new RedactionOutcome(Bytes(id), "t", [], false, DateTimeOffset.UnixEpoch));
        }

        await _store.SaveDraftAsync("s1", Draft());
        await _store.FinalizeSessionAsync("s1", new FinalizeInfo(10_000, false));
    }

    private Task<bool> AddFrameAsync(string sessionId, string id, long tsMs, bool sensitive = false) =>
        _store!.SaveRedactedFrameAsync(sessionId, new StagedFrame(id, tsMs, FrameTrigger.Click, 1600, 900, null, new byte[] { 1 }), new RedactionOutcome(Bytes(id), "t", [], sensitive, DateTimeOffset.UnixEpoch));

    private static byte[] Bytes(string id) => System.Text.Encoding.ASCII.GetBytes("jpeg-" + id);

    private sealed class FakeGateway : IPsaGateway
    {
        public string? Refusal { get; set; }

        public IReadOnlyList<TicketRow> Tickets { get; set; } = [];

        public IReadOnlyList<IntegrationInfo> Integrations { get; set; } = [];

        public List<PublishWire> Published { get; } = [];

        /// <summary>How each destination went, by its wire name. Null means everything landed.</summary>
        public Func<IReadOnlyList<string>, IReadOnlyList<PublishOutcomeRow>>? Outcomes { get; set; }

        public string? LastQuery { get; private set; }

        public Task<GatewayAnswer<IReadOnlyList<IntegrationInfo>>> IntegrationsAsync(CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IReadOnlyList<IntegrationInfo>>(r) : GatewayAnswer.Of<IReadOnlyList<IntegrationInfo>>(Integrations));

        public Task<GatewayAnswer<IReadOnlyList<TicketRow>>> SearchTicketsAsync(string query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IReadOnlyList<TicketRow>>(r) : GatewayAnswer.Of<IReadOnlyList<TicketRow>>(Tickets));
        }

        public (string Provider, string SiteUrl, string Secret)? Stored { get; private set; }

        public Task<GatewayAnswer<IReadOnlyList<IntegrationDetailRow>>> IntegrationDetailsAsync(CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IReadOnlyList<IntegrationDetailRow>>(r) : GatewayAnswer.Of<IReadOnlyList<IntegrationDetailRow>>([new IntegrationDetailRow("connectwise", "https://na.myconnectwise.net", "••••1234", DateTimeOffset.UnixEpoch, null, null)]));

        public Task<GatewayAnswer<bool>> StoreIntegrationAsync(string provider, string siteUrl, string secret, CancellationToken ct = default)
        {
            Stored = (provider, siteUrl, secret);
            return Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<bool>(r) : GatewayAnswer.Of(true));
        }

        public Task<GatewayAnswer<bool>> RemoveIntegrationAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<bool>(r) : GatewayAnswer.Of(true));

        public Task<GatewayAnswer<IntegrationCheckRow>> CheckIntegrationAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<IntegrationCheckRow>(r) : GatewayAnswer.Of(new IntegrationCheckRow(true, "Connected to https://na.myconnectwise.net.")));

        public Task<GatewayAnswer<bool>> UnmapCompanyAsync(string psaCompany, CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<bool>(r) : GatewayAnswer.Of(true));

        public Task<GatewayAnswer<CompanyMappingsAnswer>> CompanyMappingsAsync(CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<CompanyMappingsAnswer>(r) : GatewayAnswer.Of(new CompanyMappingsAnswer([new CompanyChoiceRow("7", "Acme Dental")], [])));

        public Task<GatewayAnswer<bool>> MapCompanyAsync(string psaCompany, string docCompanyId, CancellationToken ct = default) =>
            Task.FromResult(Refusal is { } r ? GatewayAnswer.Refused<bool>(r) : GatewayAnswer.Of(true));

        /// <summary>When set, a publish throws it, as a gateway with a fault of its own would.</summary>
        public Exception? Throws { get; set; }

        /// <summary>When set, a publish waits for it (or for its token) before answering, so a test can act while one is on its way.</summary>
        public Task? Hold { get; set; }

        /// <summary>Set once a publish reaches the backend.</summary>
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GatewayAnswer<IReadOnlyList<PublishOutcomeRow>>> PublishAsync(PublishWire bundle, CancellationToken ct = default)
        {
            if (Refusal is { } r)
            {
                return GatewayAnswer.Refused<IReadOnlyList<PublishOutcomeRow>>(r);
            }

            if (Throws is { } thrown)
            {
                throw thrown;
            }

            Reached.TrySetResult();
            if (Hold is { } hold)
            {
                await hold.WaitAsync(ct).ConfigureAwait(false);
            }

            Published.Add(bundle);
            return GatewayAnswer.Of<IReadOnlyList<PublishOutcomeRow>>(
                Outcomes?.Invoke(bundle.Destinations) ?? [.. bundle.Destinations.Select(d => new PublishOutcomeRow(d, true, "1"))]);
        }
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }
}
