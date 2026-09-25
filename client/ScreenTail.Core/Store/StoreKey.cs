using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace ScreenTail.Core.Store;

/// <summary>
/// Hands the store key to SQLCipher as bytes (ST-049, weaknesses P1-7).
///
/// <c>PRAGMA key = "x'…'"</c> did the same job and left two full copies of the key in immortal managed
/// strings — the hex and the command text — which outlived the <c>Array.Clear</c> calls around them and
/// survived into any crash dump or page file. The file is encrypted so that a stolen laptop yields
/// nothing (T4); a key lying in the process heap is the thing that makes that untrue.
///
/// SQLCipher reads the same <c>x'hex'</c> form through <c>sqlite3_key</c> as through the pragma and
/// treats it as a raw key, skipping the passphrase derivation the key does not need. So the hex still
/// has to exist — but as ASCII bytes in a buffer this method owns, on the stack, cleared before it
/// returns. Nothing about the key is ever a <see cref="string"/>.
/// </summary>
internal static class StoreKey
{
    /// <summary>Keys an open connection. Throws <see cref="StoreKeyException"/> when the library refuses.</summary>
    public static void Apply(SqliteConnection connection, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var handle = connection.Handle
            ?? throw new InvalidOperationException("The connection must be open before it is keyed.");

        // x'  +  two hex characters per byte  +  '
        Span<byte> material = stackalloc byte[3 + key.Length * 2];
        try
        {
            material[0] = (byte)'x';
            material[1] = (byte)'\'';
            for (var i = 0; i < key.Length; i++)
            {
                material[2 + 2 * i] = Hex(key[i] >> 4);
                material[3 + 2 * i] = Hex(key[i] & 0xF);
            }

            material[^1] = (byte)'\'';

            var rc = raw.sqlite3_key(handle, material);
            if (rc != raw.SQLITE_OK)
            {
                throw new StoreKeyException($"SQLCipher refused the key (error {rc}).");
            }
        }
        finally
        {
            material.Clear();
        }
    }

    private static byte Hex(int nibble) => (byte)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
}
