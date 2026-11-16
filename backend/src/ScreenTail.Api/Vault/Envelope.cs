using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ScreenTail.Api.Vault;

/// <summary>One sealed secret: what the row stores. Every field is safe to keep; none opens without the master key.</summary>
/// <param name="Ciphertext">The secret under the data key, tag appended.</param>
/// <param name="WrappedKey">The data key under the master key, tag appended.</param>
/// <param name="KeyId">Which master key wrapped it, so rotation knows what to rewrap.</param>
public sealed record Sealed(byte[] Ciphertext, byte[] Nonce, byte[] WrappedKey, byte[] KeyNonce, string KeyId);

/// <summary>
/// Envelope encryption for integration credentials (ST-009).
///
/// Two layers. A fresh 256-bit data key seals the secret with AES-GCM; the master key seals the data key
/// the same way. Rotating the master key is then a rewrap of forty-odd bytes per row with the secret
/// untouched, and a cloud KMS can take the outer layer over without the rows changing shape. GCM because
/// a credential decrypted with one flipped bit is a credential a provider would send: authentication is
/// the point, not an extra.
///
/// The secret's layer also authenticates where the row says it belongs (<see cref="BindingFor"/>): the
/// tenant, the provider and the site's host go in as associated data, so a sealed secret copied onto
/// another tenant's row, another provider or another host no longer opens. Each row records which form it
/// is in; rows sealed before the binding existed carry none, and <see cref="IntegrationVault"/> seals them
/// again in the bound form the first time it opens them (as do <c>--upgrade-vault-rows</c> and the key
/// rotation).
/// </summary>
public static class Envelope
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>What a row sealed with <see cref="BindingFor"/> records in <c>BindingVersion</c>. 0 is a row sealed with no binding.</summary>
    public const int BindingVersion = 1;

    /// <param name="binding">What the secret is bound to (<see cref="BindingFor"/>); empty for none.</param>
    public static Sealed Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> binding = default)
    {
        RequireKey(masterKey);
        var dataKey = RandomNumberGenerator.GetBytes(VaultOptions.KeyBytes);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var keyNonce = RandomNumberGenerator.GetBytes(NonceBytes);
            return new Sealed(
                Encrypt(dataKey, nonce, plaintext, binding),
                nonce,
                Encrypt(masterKey, keyNonce, dataKey, default),
                keyNonce,
                KeyIdOf(masterKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <param name="binding">The binding it was sealed with; empty for a row sealed with none.</param>
    /// <exception cref="CryptographicException">The wrong master key, the wrong binding, or a byte that was changed.</exception>
    public static byte[] Open(Sealed envelope, ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> binding = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        RequireKey(masterKey);
        var dataKey = Decrypt(masterKey, envelope.KeyNonce, envelope.WrappedKey, default);
        try
        {
            return Decrypt(dataKey, envelope.Nonce, envelope.Ciphertext, binding);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Rotation: the data key changes hands, the secret (and its binding) does not move.</summary>
    public static Sealed Rewrap(Sealed envelope, ReadOnlySpan<byte> oldMasterKey, ReadOnlySpan<byte> newMasterKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        RequireKey(newMasterKey);
        var dataKey = Decrypt(oldMasterKey, envelope.KeyNonce, envelope.WrappedKey, default);
        try
        {
            var keyNonce = RandomNumberGenerator.GetBytes(NonceBytes);
            return envelope with
            {
                WrappedKey = Encrypt(newMasterKey, keyNonce, dataKey, default),
                KeyNonce = keyNonce,
                KeyId = KeyIdOf(newMasterKey),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Sixteen hex characters of the key's SHA-256: stored beside every row, never the key itself.</summary>
    public static string KeyIdOf(ReadOnlySpan<byte> masterKey)
    {
        RequireKey(masterKey);
        return Convert.ToHexStringLower(SHA256.HashData(masterKey))[..16];
    }

    /// <summary>
    /// What a row's secret is bound to: a version tag, the tenant, the provider, and the site's scheme, host
    /// and port, lower-cased (the scheme too, so a row moved to plain http does not open). The path is
    /// left out, so the same site written with or without a trailing slash is the same site; another host
    /// needs the secret stored again, which the owner's route asks for anyway.
    /// </summary>
    public static byte[] BindingFor(Guid tenantId, string provider, string? siteUrl)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var host = Uri.TryCreate(siteUrl?.Trim(), UriKind.Absolute, out var site)
            ? string.Create(CultureInfo.InvariantCulture, $"{site.Scheme}://{site.IdnHost.ToLowerInvariant()}:{site.Port}")
            : string.Empty;
        return Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"screentail-vault-binding-v1\n{tenantId:D}\n{provider}\n{host}"));
    }

    private static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        using var aes = new AesGcm(key, TagBytes);
        var output = new byte[plaintext.Length + TagBytes];
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), associatedData);
        return output;
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> sealedBytes, ReadOnlySpan<byte> associatedData)
    {
        if (sealedBytes.Length < TagBytes)
        {
            throw new CryptographicException("The sealed bytes are shorter than a tag.");
        }

        using var aes = new AesGcm(key, TagBytes);
        var plaintext = new byte[sealedBytes.Length - TagBytes];
        aes.Decrypt(nonce, sealedBytes[..plaintext.Length], sealedBytes[plaintext.Length..], plaintext, associatedData);
        return plaintext;
    }

    private static void RequireKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != VaultOptions.KeyBytes)
        {
            throw new ArgumentException($"A master key is {VaultOptions.KeyBytes} bytes; this one is {key.Length}.", nameof(key));
        }
    }
}
