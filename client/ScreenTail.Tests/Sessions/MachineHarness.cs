using System.Security.Cryptography;
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

    private MachineHarness(string path, SqliteSessionStore store, SessionMachine machine)
    {
        _path = path;
        Store = store;
        Machine = machine;
    }

    public SqliteSessionStore Store { get; }

    public SessionMachine Machine { get; }

    public static async Task<MachineHarness> StartAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
        var store = await SqliteSessionStore.OpenAsync(path, new FixedKey(RandomNumberGenerator.GetBytes(32)));
        var machine = new SessionMachine(
            store,
            new NoCaptureSources(),
            new UnavailableDrafter(),
            options: new SessionMachineOptions { RedactionGrace = TimeSpan.FromMilliseconds(100), RedactionPoll = TimeSpan.FromMilliseconds(20) });
        return new MachineHarness(path, store, machine);
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
