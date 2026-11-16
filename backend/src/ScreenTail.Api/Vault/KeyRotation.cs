using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Vault;

/// <param name="Changed">Rows rewrapped under the current key or sealed again in the bound form.</param>
/// <param name="RefusedLegacy">Legacy rows left exactly as they were because <c>Vault:AllowLegacyRows</c> is off. Ids only.</param>
/// <param name="Unopenable">Legacy rows that did not open under the key they name, left as they are and reported. Ids only.</param>
public sealed record VaultRewrite(int Changed, IReadOnlyList<Guid> RefusedLegacy, IReadOnlyList<Guid> Unopenable);

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
        var unopenable = new List<Guid>();
        foreach (var row in rows)
        {
            var key = row.KeyId == fromId ? previous! : current;
            Sealed written;
            if (row.BindingVersion >= Envelope.BindingVersion)
            {
                // Bound already: only the data key changes hands, the secret is not opened.
                written = Envelope.Rewrap(
                    new Sealed(row.SecretCiphertext!, row.SecretNonce!, row.DataKeyWrapped!, row.DataKeyNonce!, row.KeyId!),
                    key,
                    current);
            }
            else if (!allowLegacy)
            {
                refused.Add(row.Id);
                continue;
            }
            else
            {
                byte[] secret;
                try
                {
                    secret = IntegrationVault.Open(row, key, allowLegacy: true);
                }
                catch (VaultRowRefusedException)
                {
                    // Reported and left as it is; the rest go on (one bad row must not stop every start).
                    unopenable.Add(row.Id);
                    continue;
                }

                try
                {
                    written = Envelope.Seal(secret, current, Envelope.BindingFor(row.TenantId, row.Provider, row.SiteUrl));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                }
            }

            if (await WriteIfUnchangedAsync(db, row, written, now, ct).ConfigureAwait(false))
            {
                changed++;
            }
        }

        return new VaultRewrite(changed, refused, unopenable);
    }

    /// <summary>
    /// Writes a row's new sealed columns only if it is still the row that was read (same id, nonce, key and
    /// form), as <see cref="IntegrationVault.UpgradeAsync"/> does: a credential the owner stored in between
    /// is newer and is kept, never overwritten with the old one.
    /// </summary>
    private static async Task<bool> WriteIfUnchangedAsync(ScreenTailContext db, Integration row, Sealed written, DateTimeOffset now, CancellationToken ct) =>
        await db.Integrations
            .Where(i => i.Id == row.Id && i.BindingVersion == row.BindingVersion && i.KeyId == row.KeyId && i.SecretNonce == row.SecretNonce)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(i => i.SecretCiphertext, written.Ciphertext)
                    .SetProperty(i => i.SecretNonce, written.Nonce)
                    .SetProperty(i => i.DataKeyWrapped, written.WrappedKey)
                    .SetProperty(i => i.DataKeyNonce, written.KeyNonce)
                    .SetProperty(i => i.KeyId, written.KeyId)
                    .SetProperty(i => i.BindingVersion, Envelope.BindingVersion)
                    .SetProperty(i => i.RotatedAt, now),
                ct)
            .ConfigureAwait(false) > 0;
}
