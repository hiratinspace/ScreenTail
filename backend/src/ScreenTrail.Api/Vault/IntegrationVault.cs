using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ScreenTrail.Api.Data;

namespace ScreenTrail.Api.Vault;

/// <summary>A credential, opened, for a provider worker. Never serialised, never logged, never returned over HTTP.</summary>
public sealed record RevealedIntegration(string Provider, string SiteUrl, string Secret);

/// <summary>
/// Where integration credentials live (ST-009). Readable only in process, by the provider workers that
/// need them; the API shows the last four characters and nothing more.
/// </summary>
public interface IIntegrationVault
{
    /// <summary>False when no master key is configured: storing is refused with a reason, listing still works.</summary>
    bool IsConfigured { get; }

    /// <summary>Creates or replaces the tenant's credential for a provider.</summary>
    /// <exception cref="VaultNotConfiguredException">No master key.</exception>
    Task StoreAsync(Guid tenantId, string provider, string siteUrl, string secret, CancellationToken ct = default);

    /// <summary>The credential, opened, or null when the tenant has none for that provider.</summary>
    Task<RevealedIntegration?> RevealAsync(Guid tenantId, string provider, CancellationToken ct = default);

    Task<bool> RemoveAsync(Guid tenantId, string provider, CancellationToken ct = default);

    /// <summary>The rows, for listing: what may be shown and nothing sealed (2026-11-15 review).</summary>
    Task<IReadOnlyList<IntegrationListing>> ListAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>One integration as a list shows it. No ciphertext, no nonce, no wrapped key: only what is safe to display.</summary>
/// <param name="SecretHint">The credential's last four characters, stored in the clear for exactly this.</param>
public sealed record IntegrationListing(string Provider, string? SiteUrl, string? SecretHint, DateTimeOffset? ConnectedAt, DateTimeOffset? LastCheckedAt, string? LastError);

/// <summary>A stored credential the vault will not open: bound to another row, or legacy while legacy rows are refused. The message names the row, never the secret.</summary>
public sealed class VaultRowRefusedException : InvalidOperationException
{
    public VaultRowRefusedException()
    {
    }

    public VaultRowRefusedException(string message)
        : base(message)
    {
    }

    public VaultRowRefusedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public sealed class VaultNotConfiguredException : InvalidOperationException
{
    public VaultNotConfiguredException()
        : base("Vault:MasterKey is not set, so credentials cannot be stored.")
    {
    }

    public VaultNotConfiguredException(string message)
        : base(message)
    {
    }

    public VaultNotConfiguredException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

public sealed class IntegrationVault(ScreenTrailContext db, VaultOptions options, TimeProvider time) : IIntegrationVault
{
    public bool IsConfigured => options.IsConfigured;

    /// <summary>
    /// Whether a credential may be stored for this site (boundary 3; 2026-10-12 review, T-177).
    ///
    /// The site is where the provider worker sends the credential, so anything but https puts a
    /// ConnectWise Basic-auth string or a Hudu key on the wire in the clear. A user name or password in
    /// the address is a second credential that would be stored unsealed and shown in full, so that is
    /// refused too. Surrounding whitespace is forgiven because the stored value is trimmed.
    /// </summary>
    public static bool IsHttpsSite([NotNullWhen(true)] string? siteUrl) =>
        Uri.TryCreate(siteUrl?.Trim(), UriKind.Absolute, out var site)
        && site.Scheme == Uri.UriSchemeHttps
        && site.Host.Length > 0
        && site.UserInfo.Length == 0;

    public async Task StoreAsync(Guid tenantId, string provider, string siteUrl, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(siteUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        // The endpoint says why in words; this is the rule for every other way in. The address is left
        // out of the message, since one that fails may be one with a password in it.
        if (!IsHttpsSite(siteUrl))
        {
            throw new ArgumentException("A credential is stored only for an absolute https site with no user name or password in it.", nameof(siteUrl));
        }

        if (!options.IsConfigured)
        {
            throw new VaultNotConfiguredException();
        }

        // Bound to the row it is written to (2026-11-15 review): this tenant, this provider, this host.
        var masterKey = options.CurrentKey();
        var sealed_ = Envelope.Seal(Encoding.UTF8.GetBytes(secret), masterKey, Envelope.BindingFor(tenantId, provider, siteUrl));
        var now = time.GetUtcNow();

        var row = await db.Integrations.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Provider == provider, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new Integration { Id = Guid.NewGuid(), TenantId = tenantId, Provider = provider };
            db.Integrations.Add(row);
        }

        // The value the rule judged, trimmed, so what is stored cannot differ from what was checked.
        row.SiteUrl = siteUrl.Trim();
        row.SecretHint = secret[^Math.Min(4, secret.Length)..];
        row.SecretCiphertext = sealed_.Ciphertext;
        row.SecretNonce = sealed_.Nonce;
        row.DataKeyWrapped = sealed_.WrappedKey;
        row.DataKeyNonce = sealed_.KeyNonce;
        row.KeyId = sealed_.KeyId;
        row.BindingVersion = Envelope.BindingVersion;
        row.ConnectedAt = now;
        row.RotatedAt = null;
        row.LastCheckedAt = null;
        row.LastError = null;
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<RevealedIntegration?> RevealAsync(Guid tenantId, string provider, CancellationToken ct = default)
    {
        var row = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Provider == provider, ct).ConfigureAwait(false);
        if (row?.SecretCiphertext is null || row.SecretNonce is null || row.DataKeyWrapped is null || row.DataKeyNonce is null || row.KeyId is null)
        {
            return null;
        }

        // The rule StoreAsync applies, applied again to what is read: a site edited to plain http (or with a
        // password in it) since is not where a credential is sent.
        if (!IsHttpsSite(row.SiteUrl))
        {
            throw new VaultRowRefusedException($"The stored {row.Provider} credential in row {row.Id} is for a site that is not an absolute https address, so it was not used. The owner can connect it again in the web admin.");
        }

        // Mid-rotation a row may still be under the previous key; it opens either way, and the rotation
        // run is what moves it. A row under a key this deployment does not hold is an error worth its
        // own words, because the alternative is a provider call with garbage.
        var masterKey = KeyFor(row.KeyId);

        // Legacy rows only exist from before the binding. Once the startup sweep has found none left, a
        // row marked legacy was written since, by something other than this service, and is not opened.
        var allowLegacy = options.AllowLegacyRows
            && (row.BindingVersion >= Envelope.BindingVersion
                || !await db.VaultStates.AsNoTracking().AnyAsync(s => s.Id == VaultState.Only && s.LegacyRowsClearedAt != null, ct).ConfigureAwait(false));
        var secret = Open(row, masterKey, allowLegacy);
        try
        {
            if (row.BindingVersion < Envelope.BindingVersion)
            {
                await UpgradeAsync(db, row, secret, options.CurrentKey(), time, ct).ConfigureAwait(false);
            }

            return new RevealedIntegration(row.Provider, row.SiteUrl ?? string.Empty, Encoding.UTF8.GetString(secret));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// A row's secret, opened in the form the row records. A bound row is opened bound and only bound: if
    /// its tenant, provider or host is not the one it was sealed for, it is refused, never tried another
    /// way. A row marked legacy (sealed before the binding existed) opens without one, while
    /// <paramref name="allowLegacy"/> allows it.
    /// </summary>
    /// <exception cref="VaultRowRefusedException">A bound row that does not open bound, or a legacy row while legacy rows are refused.</exception>
    internal static byte[] Open(Integration row, ReadOnlySpan<byte> masterKey, bool allowLegacy)
    {
        ArgumentNullException.ThrowIfNull(row);
        var sealed_ = new Sealed(row.SecretCiphertext!, row.SecretNonce!, row.DataKeyWrapped!, row.DataKeyNonce!, row.KeyId!);
        if (row.BindingVersion >= Envelope.BindingVersion)
        {
            try
            {
                return Envelope.Open(sealed_, masterKey, Envelope.BindingFor(row.TenantId, row.Provider, row.SiteUrl));
            }
            catch (CryptographicException e)
            {
                throw new VaultRowRefusedException($"The stored {row.Provider} credential in row {row.Id} does not open for this tenant, provider and site, so it was not used. The owner can connect it again in the web admin.", e);
            }
        }

        if (!allowLegacy)
        {
            throw new VaultRowRefusedException($"The stored {row.Provider} credential in row {row.Id} is marked as sealed before credentials were bound to their row, and such rows are no longer accepted (Vault:AllowLegacyRows is off, or every such row was already upgraded). The owner can connect it again in the web admin.");
        }

        try
        {
            return Envelope.Open(sealed_, masterKey);
        }
        catch (CryptographicException e)
        {
            // A bound row relabelled legacy lands here too: it does not open without its binding.
            throw new VaultRowRefusedException($"The stored {row.Provider} credential in row {row.Id} does not open, so it was not used. The owner can connect it again in the web admin.", e);
        }
    }

    /// <summary>
    /// Seals a legacy row's secret again in the bound form, under the current key. Written only if the
    /// row is still the legacy row that was read (same id, nonce and key, still marked legacy), so two
    /// readers upgrading at once write it once, and a credential stored in between is never overwritten.
    /// A failure to write is not a failure to read: the row is upgraded next time.
    /// </summary>
    /// <returns>Whether this call wrote the bound form.</returns>
    internal static async Task<bool> UpgradeAsync(ScreenTrailContext db, Integration row, byte[] secret, byte[] currentKey, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(row);
        var bound = Envelope.Seal(secret, currentKey, Envelope.BindingFor(row.TenantId, row.Provider, row.SiteUrl));
        var now = time.GetUtcNow();
        try
        {
            return await db.Integrations
                .Where(i => i.Id == row.Id && i.BindingVersion == row.BindingVersion && i.KeyId == row.KeyId && i.SecretNonce == row.SecretNonce)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(i => i.SecretCiphertext, bound.Ciphertext)
                        .SetProperty(i => i.SecretNonce, bound.Nonce)
                        .SetProperty(i => i.DataKeyWrapped, bound.WrappedKey)
                        .SetProperty(i => i.DataKeyNonce, bound.KeyNonce)
                        .SetProperty(i => i.KeyId, bound.KeyId)
                        .SetProperty(i => i.BindingVersion, Envelope.BindingVersion)
                        .SetProperty(i => i.RotatedAt, now),
                    ct)
                .ConfigureAwait(false) > 0;
        }
        catch (DbException)
        {
            return false;
        }
    }

    public async Task<bool> RemoveAsync(Guid tenantId, string provider, CancellationToken ct = default) =>
        await db.Integrations.Where(i => i.TenantId == tenantId && i.Provider == provider).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;

    // Projected, so the sealed columns never leave the database for a list (2026-11-15 review).
    public async Task<IReadOnlyList<IntegrationListing>> ListAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Integrations.AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .OrderBy(i => i.Provider)
            .Select(i => new IntegrationListing(i.Provider, i.SiteUrl, i.SecretHint, i.ConnectedAt, i.LastCheckedAt, i.LastError))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    private byte[] KeyFor(string keyId)
    {
        if (options.IsConfigured)
        {
            var current = options.CurrentKey();
            if (Envelope.KeyIdOf(current) == keyId)
            {
                return current;
            }

            if (options.PreviousKey() is { } previous && Envelope.KeyIdOf(previous) == keyId)
            {
                return previous;
            }
        }

        throw new VaultRowRefusedException($"The credential is wrapped under master key {keyId}, which this deployment does not hold.");
    }
}
