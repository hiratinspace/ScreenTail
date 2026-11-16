using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Vault;

/// <summary>
/// Rotating the master key (ST-009 AC3). Every row wrapped under <c>Vault:PreviousMasterKey</c> is rewrapped
/// under <c>Vault:MasterKey</c>; the secrets themselves are not touched. Run with <c>--rotate-vault-keys</c>
/// after deploying the new key; a second run finds nothing, which is the check that the first finished.
/// docs/security/key-rotation.md has the steps in order.
///
/// A row sealed before secrets were bound to their row (<see cref="Envelope.BindingFor"/>, 2026-11-15
/// review) is sealed again here, under the current key and in the bound form, whichever key it was under.
/// That is the one case where the secret is opened and sealed anew rather than only rewrapped.
/// <see cref="UpgradeLegacyAsync"/> does that part alone, with no rotation, for <c>--upgrade-vault-rows</c>.
/// </summary>
public static class KeyRotation
{
    /// <returns>How many rows changed: rewrapped, or sealed again in the bound form.</returns>
    public static Task<int> RotateAsync(ScreenTailContext db, VaultOptions options, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        var previous = options.PreviousKey()
            ?? throw new InvalidOperationException("Vault:PreviousMasterKey is not set; there is no key to rotate from.");
        return RewriteAsync(db, options.CurrentKey(), previous, time, ct);
    }

    /// <summary>Seals every legacy row again in the bound form, under the current key, opening it under whichever configured key it is wrapped with.</summary>
    /// <returns>How many rows were upgraded. A second run returns 0.</returns>
    public static Task<int> UpgradeLegacyAsync(ScreenTailContext db, VaultOptions options, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsConfigured)
        {
            throw new VaultNotConfiguredException();
        }

        return RewriteAsync(db, options.CurrentKey(), options.PreviousKey(), time, ct);
    }

    /// <param name="previous">The key to rewrap from, or null to upgrade legacy rows only.</param>
    private static async Task<int> RewriteAsync(ScreenTailContext db, byte[] current, byte[]? previous, TimeProvider? time, CancellationToken ct)
    {
        var currentId = Envelope.KeyIdOf(current);
        var fromId = previous is null ? null : Envelope.KeyIdOf(previous);
        var now = (time ?? TimeProvider.System).GetUtcNow();

        // Rows under the previous key, and legacy rows under either key. A bound row under the current
        // key has nothing to change and is not opened.
        var rows = await db.Integrations
            .Where(i => i.KeyId == currentId || (fromId != null && i.KeyId == fromId))
            .Where(i => (fromId != null && i.KeyId == fromId) || i.BindingVersion < Envelope.BindingVersion)
            .Where(i => i.SecretCiphertext != null && i.SecretNonce != null && i.DataKeyWrapped != null && i.DataKeyNonce != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var row in rows)
        {
            var key = row.KeyId == fromId ? previous! : current;
            if (row.BindingVersion >= Envelope.BindingVersion)
            {
                // Bound already: only the data key changes hands, the secret is not opened.
                var rewrapped = Envelope.Rewrap(
                    new Sealed(row.SecretCiphertext!, row.SecretNonce!, row.DataKeyWrapped!, row.DataKeyNonce!, row.KeyId!),
                    key,
                    current);
                row.DataKeyWrapped = rewrapped.WrappedKey;
                row.DataKeyNonce = rewrapped.KeyNonce;
                row.KeyId = rewrapped.KeyId;
                row.RotatedAt = now;
                continue;
            }

            byte[] secret;
            try
            {
                secret = IntegrationVault.Open(row, key, allowLegacy: true);
            }
            catch (CryptographicException e)
            {
                // Nothing has been saved yet, so stopping here leaves every row as it was.
                throw new InvalidOperationException($"The {row.Provider} credential in row {row.Id} does not open under master key {row.KeyId}, so nothing was changed. Store that credential again, then run this again.", e);
            }

            try
            {
                var bound = Envelope.Seal(secret, current, Envelope.BindingFor(row.TenantId, row.Provider, row.SiteUrl));
                row.SecretCiphertext = bound.Ciphertext;
                row.SecretNonce = bound.Nonce;
                row.DataKeyWrapped = bound.WrappedKey;
                row.DataKeyNonce = bound.KeyNonce;
                row.KeyId = bound.KeyId;
                row.BindingVersion = Envelope.BindingVersion;
                row.RotatedAt = now;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return rows.Count;
    }
}
