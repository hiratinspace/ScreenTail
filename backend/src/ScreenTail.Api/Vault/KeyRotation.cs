using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Vault;

/// <summary>
/// Rotating the master key (ST-009 AC3). Every row wrapped under <c>Vault:PreviousMasterKey</c> is rewrapped
/// under <c>Vault:MasterKey</c>; the secrets themselves are not touched. Run with <c>--rotate-vault-keys</c>
/// after deploying the new key; a second run finds nothing, which is the check that the first finished.
/// docs/security/key-rotation.md has the steps in order.
/// </summary>
public static class KeyRotation
{
    /// <returns>How many rows changed hands.</returns>
    public static async Task<int> RotateAsync(ScreenTailContext db, VaultOptions options, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        var previous = options.PreviousKey()
            ?? throw new InvalidOperationException("Vault:PreviousMasterKey is not set; there is no key to rotate from.");
        var current = options.CurrentKey();
        var fromId = Envelope.KeyIdOf(previous);
        var now = (time ?? TimeProvider.System).GetUtcNow();

        var rows = await db.Integrations.Where(i => i.KeyId == fromId).ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var rewrapped = Envelope.Rewrap(
                new Sealed(row.SecretCiphertext!, row.SecretNonce!, row.DataKeyWrapped!, row.DataKeyNonce!, row.KeyId!),
                previous,
                current);
            row.DataKeyWrapped = rewrapped.WrappedKey;
            row.DataKeyNonce = rewrapped.KeyNonce;
            row.KeyId = rewrapped.KeyId;
            row.RotatedAt = now;
        }

        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }
}
