using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ScreenTail.Core.Store;
using ScreenTail.Tests.Security;

namespace ScreenTail.Tests.Store;

/// <summary>
/// How the store key reaches SQLCipher (ST-049, weaknesses P1-7).
///
/// The key used to be interpolated into <c>PRAGMA key = "x'…'"</c>. <c>OpenAsync</c> and the DPAPI
/// provider both clear their byte copies, and it bought nothing: the hex string and the command text are
/// immutable managed strings that live until the garbage collector gets to them, and after that in a
/// crash dump or the page file. Two full-fidelity copies of the key outlived the hygiene written to
/// prevent exactly that. T4 — a stolen laptop — is the store's headline threat, and the whole reason the
/// key is under DPAPI is that the file alone must be useless.
///
/// The key is now handed to the library as bytes in a buffer this code owns and clears. There is no
/// string.
/// </summary>
public sealed class StoreKeyTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"screentail-key-{Guid.NewGuid():N}.db");
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task TheBytesOpenWhatTheStoreWrote()
    {
        // The same bytes, handed over the same way the store hands them, open the store's own file. This
        // is the half of the change that could quietly break: SQLCipher reading the buffer as a passphrase
        // rather than a raw key would derive a different key and refuse the file, or worse, create a
        // second one.
        await CreateStoreAsync();

        await using var connection = await OpenRawAsync(_key);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM sqlite_master";

        Assert.True((long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0);
    }

    [Fact]
    public async Task TheWrongBytesAreRefusedByTheDatabase()
    {
        await CreateStoreAsync();

        await using var connection = await OpenRawAsync(RandomNumberGenerator.GetBytes(32));
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM sqlite_master";

        // 26: "file is not a database". The library accepted the key; the file did not.
        var refused = await Assert.ThrowsAsync<SqliteException>(
            async () => await count.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Equal(26, refused.SqliteErrorCode);
    }

    [Fact]
    public async Task AConnectionThatIsNotOpenCannotBeKeyed()
    {
        // The handle does not exist before Open, and keying nothing must say so rather than succeed.
        await using var connection = new SqliteConnection($"Data Source={_path}");

        Assert.Throws<InvalidOperationException>(() => StoreKey.Apply(connection, _key));
    }

    private async Task CreateStoreAsync()
    {
        var store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(_key));
        await store.DisposeAsync();
    }

    private async Task<SqliteConnection> OpenRawAsync(byte[] key)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        StoreKey.Apply(connection, key);
        return connection;
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public ValueTask DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            File.Delete(_path + suffix);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Source, not behaviour: the interpolation is the bug and no test of the store's behaviour can see
    /// it, because the store works perfectly either way. This looks at the real files, the way
    /// <see cref="ReleaseSurfaceTests"/> does, so the change cannot come back in a hurry — a
    /// <c>PRAGMA rekey</c> written the old way would be the same bug twice.
    /// </summary>
    [Theory]
    [InlineData("ScreenTail.Core")]
    [InlineData("ScreenTail.Service")]
    [InlineData("ScreenTail.Platform")]
    public void NoStatementEverCarriesTheKey(string project)
    {
        var files = ClientSources.Of(project);
        Assert.True(files.Count > 0, $"No sources found for {project}.");

        // Code, not comments: the comments are allowed to say what was wrong.
        var offenders = files
            .Where(file => File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Any(line => line.Contains("PRAGMA key", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("PRAGMA rekey", StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0, $"The key is built into a statement in: {string.Join(", ", offenders)}");
    }
}
