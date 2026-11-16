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
/// A row sealed before secrets were bound to their row (<see cref="Envelope.BindingFor"/>) is sealed
/// again here, under the current key and in the bound form, whichever key it was under. That is the one
/// case where the secret is opened and sealed anew rather than only rewrapped.
/// </summary>
public static class KeyRotation
{
    /// <returns>How many rows changed: rewrapped, or sealed again in the bound form.</returns>
    public static async Task<int> RotateAsync(ScreenTailContext db, VaultOptions options, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        var previous = options.PreviousKey()
            ?? throw new InvalidOperationException("Vault:PreviousMasterKey is not set; there is no key to rotate from.");
        var current = options.CurrentKey();
        var fromId = Envelope.KeyIdOf(previous);
        var currentId = Envelope.KeyIdOf(current);
        var now = (time ?? TimeProvider.System).GetUtcNow();

        var rows = await db.Integrations
            .Where(i => i.KeyId == fromId || i.KeyId == currentId)
            .Where(i => i.SecretCiphertext != null && i.SecretNonce != null && i.DataKeyWrapped != null && i.DataKeyNonce != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var changed = 0;
        foreach (var row in rows)
        {
            var underPrevious = row.KeyId == fromId;
            byte[] secret;
            bool legacy;
            try
            {
                secret = IntegrationVault.Open(row, underPrevious ? previous : current, out legacy);
            }
            catch (CryptographicException e)
            {
                // Nothing has been saved yet, so stopping here leaves every row as it was.
                throw new InvalidOperationException($"The {row.Provider} credential in row {row.Id} does not open under master key {row.KeyId}, so nothing was rotated. Store that credential again, then run the rotation again.", e);
            }

            try
            {
                if (legacy)
                {
                    // Sealed again, bound to this row, under the current key.
                    var bound = Envelope.Seal(secret, current, Envelope.BindingFor(row.TenantId, row.Provider, row.SiteUrl));
                    row.SecretCiphertext = bound.Ciphertext;
                    row.SecretNonce = bound.Nonce;
                    row.DataKeyWrapped = bound.WrappedKey;
                    row.DataKeyNonce = bound.KeyNonce;
                    row.KeyId = bound.KeyId;
                }
                else if (underPrevious)
                {
                    var rewrapped = Envelope.Rewrap(
                        new Sealed(row.SecretCiphertext!, row.SecretNonce!, row.DataKeyWrapped!, row.DataKeyNonce!, row.KeyId!),
                        previous,
                        current);
                    row.DataKeyWrapped = rewrapped.WrappedKey;
                    row.DataKeyNonce = rewrapped.KeyNonce;
                    row.KeyId = rewrapped.KeyId;
                }
                else
                {
                    // Bound and under the current key already: nothing to do.
                    continue;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            row.RotatedAt = now;
            changed++;
        }

        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return changed;
    }
}
