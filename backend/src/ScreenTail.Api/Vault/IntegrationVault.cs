using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Vault;

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

    /// <summary>The rows, for listing. Ciphertext comes along; the caller shows the hint.</summary>
    Task<IReadOnlyList<Integration>> ListAsync(Guid tenantId, CancellationToken ct = default);
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

public sealed class IntegrationVault(ScreenTailContext db, VaultOptions options, TimeProvider time) : IIntegrationVault
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

        var masterKey = options.CurrentKey();
        var sealed_ = Envelope.Seal(Encoding.UTF8.GetBytes(secret), masterKey);
        var now = time.GetUtcNow();

        var row = await db.Integrations.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Provider == provider, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new Integration { Id = Guid.NewGuid(), TenantId = tenantId, Provider = provider };
            db.Integrations.Add(row);
        }

        row.SiteUrl = siteUrl;
        row.SecretHint = secret[^Math.Min(4, secret.Length)..];
        row.SecretCiphertext = sealed_.Ciphertext;
        row.SecretNonce = sealed_.Nonce;
        row.DataKeyWrapped = sealed_.WrappedKey;
        row.DataKeyNonce = sealed_.KeyNonce;
        row.KeyId = sealed_.KeyId;
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

        // Mid-rotation a row may still be under the previous key; it opens either way, and the rotation
        // run is what moves it. A row under a key this deployment does not hold is an error worth its
        // own words, because the alternative is a provider call with garbage.
        var masterKey = KeyFor(row.KeyId);
        var sealed_ = new Sealed(row.SecretCiphertext, row.SecretNonce, row.DataKeyWrapped, row.DataKeyNonce, row.KeyId);
        return new RevealedIntegration(row.Provider, row.SiteUrl ?? string.Empty, Encoding.UTF8.GetString(Envelope.Open(sealed_, masterKey)));
    }

    public async Task<bool> RemoveAsync(Guid tenantId, string provider, CancellationToken ct = default) =>
        await db.Integrations.Where(i => i.TenantId == tenantId && i.Provider == provider).ExecuteDeleteAsync(ct).ConfigureAwait(false) > 0;

    public async Task<IReadOnlyList<Integration>> ListAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Integrations.AsNoTracking().Where(i => i.TenantId == tenantId).OrderBy(i => i.Provider).ToListAsync(ct).ConfigureAwait(false);

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

        throw new InvalidOperationException($"The credential is wrapped under master key {keyId}, which this deployment does not hold.");
    }
}
