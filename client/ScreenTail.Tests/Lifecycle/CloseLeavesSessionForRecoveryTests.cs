using System.Security.Cryptography;
using ScreenTail.Core.Lifecycle;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Lifecycle;

/// <summary>
/// What a close Windows asked for leaves behind (ST-112): the stop the capture service always does, in its
/// order (capture first, then the machine, then the store), over a real store; and a session that was
/// recording left exactly as the existing recovery path expects to find it, so the next start finalizes it
/// as partial (ST-020). No new session semantics: the close never stops, finalizes or discards a session.
/// </summary>
public sealed class CloseLeavesSessionForRecoveryTests : IAsyncDisposable
{
    private static readonly RemoteTool ScreenConnect = new() { Kind = RemoteToolKind.Screenconnect, ClientVersion = "24.1" };
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly FixedKey _key = new(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public async Task ARecordingClosedByRestartManagerIsStoppedInOrderAndRecoveredAsPartialAtTheNextStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await SqliteSessionStore.OpenAsync(_path, _key, ct: ct);
        var machine = new SessionMachine(store, new NoCaptureSources(), new UnavailableDrafter());
        Assert.True(await machine.StartAsync(ScreenConnect, ct: ct));
        var sessionId = machine.SessionId!;

        // The host as CaptureHost runs it: a capture loop writing until the service stops, and on the way
        // out the loops awaited, then the machine and the store closed, and only then "stopped".
        var order = new List<string>();
        using var stopping = new CancellationTokenSource();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capturing = Task.Run(
            async () =>
            {
                var ts = 1_000L;
                try
                {
                    while (true)
                    {
                        _ = await machine.TryRecordEventAsync(new ClickEvent { TsMs = ts += 100, X = 1, Y = 1, Button = MouseButton.Left }, stopping.Token);
                        await Task.Delay(10, stopping.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (order)
                    {
                        order.Add("capture");
                    }
                }
            },
            ct);
        var host = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, stopping.Token);
                }
                catch (OperationCanceledException)
                {
                }

                await capturing;
                await machine.DisposeAsync();
                order.Add("machine");
                await store.DisposeAsync();
                order.Add("store");
                stopped.SetResult();
            },
            ct);

        // Let it record a little, so there is something for recovery to find.
        await Task.Delay(100, ct);
        var requests = new CloseRequests(stopping.Cancel, stopped.Task);
        Assert.True(CloseRequests.MayEnd(CloseRequest.FromEndSession(0x1)));
        var outcome = await Task.Factory.StartNew(() => requests.Ended(ending: true, CloseRequest.FromEndSession(0x1)), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            .WaitAsync(TimeSpan.FromSeconds(10), ct);
        await host.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(CloseOutcome.Stopped, outcome);
        Assert.Equal(["capture", "machine", "store"], order);

        // The store closed cleanly: its write-ahead log was folded in and removed, which a process ended
        // mid-write leaves behind.
        Assert.False(File.Exists(_path + "-wal"), "the write-ahead log is left");
        Assert.False(File.Exists(_path + "-shm"), "the shared-memory file is left");

        // The next start: the session is still the orphan recovery looks for, and is finalized as partial.
        await using var reopened = await SqliteSessionStore.OpenAsync(_path, _key, ct: ct);
        Assert.Equal([sessionId], await reopened.ListSessionsInStatesAsync(SessionStateNames.Orphanable, ct));
        Assert.Equal(CaptureStates.Recording, (await reopened.GetSessionSummaryAsync(sessionId, ct))!.State);

        await using var restarted = new SessionMachine(reopened, new NoCaptureSources(), new UnavailableDrafter());
        Assert.Equal(1, await restarted.RecoverAsync(ct));
        var recovered = (await reopened.LoadSessionAsync(sessionId, ct))!;
        Assert.True(recovered.PartialCapture);
        Assert.True(recovered.DurationMs > 1_000, $"duration {recovered.DurationMs} ms: the clicks before the close were kept");
        Assert.Empty(await reopened.ListSessionsInStatesAsync(SessionStateNames.Orphanable, ct));

        // Restarted, by the next sign-in or by Restart Manager after another program's installer (the restart
        // is the ordinary start, ApplicationRestart): watching, not recording. Nothing was resumed.
        Assert.NotEqual(SessionState.Recording, restarted.State);
        Assert.Null(restarted.Hearing);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }

        return ValueTask.CompletedTask;
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}
