using System.Security.Cryptography;
using ScreenTail.Core.Capture;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;

namespace ScreenTail.Tests.Sessions;

/// <summary>
/// A state machine over a real (temporary) encrypted store, for tests that care about what the machine
/// records rather than about the transitions themselves. <see cref="StateMachineTests"/> keeps its own
/// fakes, which count calls and vary the drafting outcome.
/// </summary>
internal sealed class MachineHarness : IAsyncDisposable
{
    private readonly string _path;

    private MachineHarness(string path, SqliteSessionStore store, SessionMachine machine, PendingFrames pending)
    {
        _path = path;
        Store = store;
        Machine = machine;
        Pending = pending;
    }

    /// <summary>
    /// Where a captured frame waits to be read (ADR-0006).
    ///
    /// The store used to hold it with <c>redaction_pending = 1</c>, so a test asking "was this frame
    /// accepted?" asked the store. It asks this now.
    /// </summary>
    public PendingFrames Pending { get; }

    public SqliteSessionStore Store { get; }

    public SessionMachine Machine { get; }

    /// <param name="time">A hand-moved clock for tests that assert durations, so they measure the
    /// machine's accounting rather than the runner's scheduler.</param>
    public static async Task<MachineHarness> StartAsync(TimeProvider? time = null)
    {
        var path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
        var store = await SqliteSessionStore.OpenAsync(path, new FixedKey(RandomNumberGenerator.GetBytes(32)));

        // Deep, so a test that captures a handful of frames is not measuring the production depth of
        // four; PendingFramesTests owns that number.
        var pending = new PendingFrames(depth: 1000);
        var machine = new SessionMachine(
            store,
            new NoCaptureSources(),
            new UnavailableDrafter(),
            time: time,
            options: new SessionMachineOptions
            {
                RedactionGrace = TimeSpan.FromMilliseconds(100),
                RedactionPoll = TimeSpan.FromMilliseconds(20),
                Pending = pending,
            });
        return new MachineHarness(path, store, machine, pending);
    }

    public async ValueTask DisposeAsync()
    {
        await Machine.DisposeAsync();
        await Store.DisposeAsync();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }
}
