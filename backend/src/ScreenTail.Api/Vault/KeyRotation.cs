using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Vault;

/// <param name="Changed">Rows rewrapped under the current key or sealed again in the bound form.</param>
/// <param name="RefusedLegacy">Legacy rows left exactly as they were because <c>Vault:AllowLegacyRows</c> is off. Ids only.</param>
public sealed record VaultRewrite(int Changed, IReadOnlyList<Guid> RefusedLegacy);

/// <summary>
/// Rotating the master key (ST-009 AC3). Every row wrapped under <c>Vault:PreviousMasterKey</c> is rewrapped
/// under <c>Vault:MasterKey</c>; the secrets themselves are not touched. Run with <c>--rotate-vault-keys</c>
/// after deploying the new key; a second run finds nothing, which is the check that the first finished.
/// docs/security/key-rotation.md has the steps in order.
///
/// A row sealed before secrets were bound to their row (<see cref="Envelope.BindingFor"/>, 2026-11-15
/// review) is sealed again here, under the current key and in the bound form, whichever key it was under,
/// but only while <c>Vault:AllowLegacyRows</c> is on: with it off a legacy row is refused here exactly as a
/// read refuses it, left untouched and reported by id, so a row marked legacy by a database writer is
/// never turned into a bound one. <see cref="UpgradeLegacyAsync"/> does the upgrade alone, with no
/// rotation, for <c>--upgrade-vault-rows</c> and the startup sweep (<see cref="VaultSweep"/>).
/// </summary>
public static class KeyRotation
{
    public static Task<VaultRewrite> RotateAsync(ScreenTailContext db, VaultOptions options, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        var previous = options.PreviousKey()
            ?? throw new InvalidOperationException("Vault:PreviousMasterKey is not set; there is no key to rotate from.");
        return RewriteAsync(db, options.CurrentKey(), previous, rewrapPrevious: true, options.AllowLegacyRows, time, ct);
    }

    /// <summary>Seals every legacy row again in the bound form, under the current key, opening it under whichever configured key it is wrapped with.</summary>
    /// <returns>How many rows were upgraded (a second run upgrades none), and any refused because legacy rows are off.</returns>
    public static Task<VaultRewrite> UpgradeLegacyAsync(ScreenTailContext db, VaultOptions options, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsConfigured)
        {
            throw new VaultNotConfiguredException();
        }

        return RewriteAsync(db, options.CurrentKey(), options.PreviousKey(), rewrapPrevious: false, options.AllowLegacyRows, time, ct);
    }

    /// <param name="previous">The previous key, if configured: legacy rows under it open with it.</param>
    /// <param name="rewrapPrevious">Whether bound rows under <paramref name="previous"/> are rewrapped too (the rotation) or left alone (the upgrade alone).</param>
    private static async Task<VaultRewrite> RewriteAsync(ScreenTailContext db, byte[] current, byte[]? previous, bool rewrapPrevious, bool allowLegacy, TimeProvider? time, CancellationToken ct)
    {
        var currentId = Envelope.KeyIdOf(current);
        var fromId = previous is null ? null : Envelope.KeyIdOf(previous);
        var rewrapId = rewrapPrevious ? fromId : null;
        var now = (time ?? TimeProvider.System).GetUtcNow();

        // Once the sweep has found no legacy row left, one marked legacy was written since: refused, as a
        // read refuses it (VaultSweep).
        allowLegacy = allowLegacy && !await db.VaultStates.AsNoTracking().AnyAsync(s => s.Id == VaultState.Only && s.LegacyRowsClearedAt != null, ct).ConfigureAwait(false);

        // Bound rows under the previous key when rotating, and legacy rows under either key. A bound row
        // under the current key has nothing to change and is not opened.
        var rows = await db.Integrations
            .Where(i => i.KeyId == currentId || (fromId != null && i.KeyId == fromId))
            .Where(i => (rewrapId != null && i.KeyId == rewrapId) || i.BindingVersion < Envelope.BindingVersion)
            .Where(i => i.SecretCiphertext != null && i.SecretNonce != null && i.DataKeyWrapped != null && i.DataKeyNonce != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var changed = 0;
        var refused = new List<Guid>();
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
                changed++;
                continue;
            }

            if (!allowLegacy)
            {
                refused.Add(row.Id);
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
                changed++;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new VaultRewrite(changed, refused);
    }
}
