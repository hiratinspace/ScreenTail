using System.Runtime.Versioning;
using System.Security.Cryptography;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Host;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Host;

/// <summary>
/// ST-085: the answers the UI gets to the questions only the service can answer.
///
/// Against the real encrypted store, because the claim worth testing is about what leaves it. INV-1 says
/// nothing outside the redaction worker may see a pending frame, and the History screen counting one
/// would be that rule broken in the least visible way — a number on a screen, with no image anywhere near
/// it.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class CaptureControllerTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;

    [Fact]
    public async Task APendingFrameIsNotCountedOnTheHistoryScreen()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await store.CreateSessionAsync(new NewSession("s1", At, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null), ct);
        await store.StageFrameAsync("s1", new StagedFrame("f1", 1_000, FrameTrigger.Click, 80, 60, null, new byte[] { 1, 2, 3 }), ct);

        var reply = await ListAsync(store, ct);

        var row = Assert.Single(reply.Sessions);
        Assert.Equal("s1", row.Id);

        // Staged and never redacted, so it is not a frame anything outside redaction may count.
        Assert.Equal(0, row.Frames);
    }

    [Fact]
    public async Task ASessionWithNoDraftIsPendingRatherThanBlank()
    {
        // ST-079's status rule, computed by the service so the draft's text never crosses the pipe just
        // to be turned back into a single word.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        await store.CreateSessionAsync(new NewSession("s1", At, new RemoteTool { Kind = RemoteToolKind.Screenconnect }, false, null), ct);

        var reply = await ListAsync(store, ct);

        Assert.Equal("pending", Assert.Single(reply.Sessions).Status);
        Assert.Equal("screenconnect", Assert.Single(reply.Sessions).Tool);
    }

    [Fact]
    public async Task TheDiagnosticsPanelIsAnsweredFromTheService()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        var controller = Controller(store, () => new DiagnosticsReported
        {
            Scope = "Capturing — ScreenConnect",
            RedactionBacklog = 4,
            FramesDropped = 2,
            KeystrokesDropped = 17,
            EgressBlocked = 0,
            LocalOnly = true,
            PolicyVersion = "local",
            CpuPercent = 0,
            WorkingSetBytes = 1024,
            ServiceVersion = "0.1.0",
        });

        var reply = await controller.ReplyToAsync(new GetDiagnosticsCommand { RequestId = 1 }, ct);

        var diagnostics = Assert.IsType<DiagnosticsReported>(reply);
        Assert.Equal("Capturing — ScreenConnect", diagnostics.Scope);
        Assert.True(diagnostics.LocalOnly);
    }

    [Fact]
    public async Task DeleteEverythingReachesTheEraserWhenItIsConfirmed()
    {
        // The path existed and was unreachable: nothing in the UI and no command on the pipe called it,
        // so INV-12's "delete everything" was a promise the product could not keep.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        var asked = false;
        var controller = Controller(store, erase: _ =>
        {
            asked = true;
            return Task.FromResult(true);
        });

        var result = await controller.HandleAsync(
            new EraseAllLocalDataCommand { RequestId = 1, Confirmation = await TokenAsync(controller, "erase_everything", ct) },
            ct);

        Assert.True(result.Ok);
        Assert.True(asked);
    }

    [Fact]
    public async Task DeleteEverythingWithNothingBehindItErasesNothing()
    {
        // 2026-09-19 review. The command had no fields at all, so one frame on the pipe wiped the store —
        // and the pipe only proves the peer is the same user, which a compromised same-user process is.
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        var asked = false;
        var controller = Controller(store, erase: _ =>
        {
            asked = true;
            return Task.FromResult(true);
        });

        var result = await controller.HandleAsync(new EraseAllLocalDataCommand { RequestId = 1 }, ct);

        Assert.False(result.Ok);
        Assert.False(asked);
    }

    [Fact]
    public async Task ATokenIsGoodForOneEraseAndNoMore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        var erased = 0;
        var controller = Controller(store, erase: _ =>
        {
            erased++;
            return Task.FromResult(true);
        });

        var token = await TokenAsync(controller, "erase_everything", ct);
        _ = await controller.HandleAsync(new EraseAllLocalDataCommand { RequestId = 1, Confirmation = token }, ct);
        var again = await controller.HandleAsync(new EraseAllLocalDataCommand { RequestId = 2, Confirmation = token }, ct);

        Assert.False(again.Ok);
        Assert.Equal(1, erased);
    }

    [Fact]
    public async Task ConfirmingADiscardDoesNotAuthoriseErasingEverything()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await OpenAsync(ct);
        var asked = false;
        var controller = Controller(store, erase: _ =>
        {
            asked = true;
            return Task.FromResult(true);
        });

        var token = await TokenAsync(controller, "discard_session", ct);
        var result = await controller.HandleAsync(new EraseAllLocalDataCommand { RequestId = 1, Confirmation = token }, ct);

        Assert.False(result.Ok);
        Assert.False(asked);
    }

    [Fact]
    public async Task TheServiceSaysWhatHasToBeTyped()
    {
        // The phrase comes from the service so that the words a customer is shown and the words the
        // service expects cannot drift apart.
        var ct = TestContext.Current.CancellationToken;
        var controller = Controller(await OpenAsync(ct));

        var discard = Assert.IsType<ConfirmationIssued>(
            await controller.ReplyToAsync(new RequestConfirmationCommand { RequestId = 1, Action = "discard_session" }, ct));
        var erase = Assert.IsType<ConfirmationIssued>(
            await controller.ReplyToAsync(new RequestConfirmationCommand { RequestId = 2, Action = "erase_everything" }, ct));

        Assert.Equal("DISCARD", discard.Phrase);
        Assert.Equal("DELETE EVERYTHING", erase.Phrase);
        Assert.NotEqual(discard.Token, erase.Token);
    }

    [Fact]
    public async Task AConfirmationForSomethingNobodyDefinedIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var controller = Controller(await OpenAsync(ct));

        Assert.Null(await controller.ReplyToAsync(
            new RequestConfirmationCommand { RequestId = 1, Action = "erase_everything_please" },
            ct));
    }

    private static async Task<string> TokenAsync(CaptureController controller, string action, CancellationToken ct)
    {
        var issued = await controller.ReplyToAsync(new RequestConfirmationCommand { RequestId = 99, Action = action }, ct);
        return Assert.IsType<ConfirmationIssued>(issued).Token;
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

    private static async Task<SessionsListed> ListAsync(SqliteSessionStore store, CancellationToken ct)
    {
        var reply = await Controller(store).ReplyToAsync(new ListSessionsCommand { RequestId = 1 }, ct);
        return Assert.IsType<SessionsListed>(reply);
    }

    private static CaptureController Controller(
        SqliteSessionStore store,
        Func<DiagnosticsReported>? diagnostics = null,
        Func<CancellationToken, Task<bool>>? erase = null) =>
        new(
            new SessionMachine(store, new NoCaptureSources(), new UnavailableDrafter()),
            new AlwaysCapableProbe(),
            store,
            diagnostics ?? (() => throw new InvalidOperationException("not expected")),
            erase ?? (_ => Task.FromResult(false)));

    private async Task<SqliteSessionStore> OpenAsync(CancellationToken ct) =>
        _store ??= await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)), ct: ct);

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    private sealed class AlwaysCapableProbe : ICapabilityProbe
    {
        public CapabilityReport Probe() => new(DateTimeOffset.UnixEpoch, Array.Empty<CapabilityCheck>());
    }
}
