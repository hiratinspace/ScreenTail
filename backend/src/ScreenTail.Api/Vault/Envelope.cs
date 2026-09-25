using System.Security.Cryptography;

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
/// </summary>
public static class Envelope
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public static Sealed Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> masterKey)
    {
        RequireKey(masterKey);
        var dataKey = RandomNumberGenerator.GetBytes(VaultOptions.KeyBytes);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var keyNonce = RandomNumberGenerator.GetBytes(NonceBytes);
            return new Sealed(
                Encrypt(dataKey, nonce, plaintext),
                nonce,
                Encrypt(masterKey, keyNonce, dataKey),
                keyNonce,
                KeyIdOf(masterKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <exception cref="CryptographicException">The wrong master key, or a byte that was changed.</exception>
    public static byte[] Open(Sealed envelope, ReadOnlySpan<byte> masterKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        RequireKey(masterKey);
        var dataKey = Decrypt(masterKey, envelope.KeyNonce, envelope.WrappedKey);
        try
        {
            return Decrypt(dataKey, envelope.Nonce, envelope.Ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Rotation: the data key changes hands, the secret does not move.</summary>
    public static Sealed Rewrap(Sealed envelope, ReadOnlySpan<byte> oldMasterKey, ReadOnlySpan<byte> newMasterKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        RequireKey(newMasterKey);
        var dataKey = Decrypt(oldMasterKey, envelope.KeyNonce, envelope.WrappedKey);
        try
        {
            var keyNonce = RandomNumberGenerator.GetBytes(NonceBytes);
            return envelope with
            {
                WrappedKey = Encrypt(newMasterKey, keyNonce, dataKey),
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

    private static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext)
    {
        using var aes = new AesGcm(key, TagBytes);
        var output = new byte[plaintext.Length + TagBytes];
        aes.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length));
        return output;
    }

    private static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> sealedBytes)
    {
        if (sealedBytes.Length < TagBytes)
        {
            throw new CryptographicException("The sealed bytes are shorter than a tag.");
        }

        using var aes = new AesGcm(key, TagBytes);
        var plaintext = new byte[sealedBytes.Length - TagBytes];
        aes.Decrypt(nonce, sealedBytes[..plaintext.Length], sealedBytes[plaintext.Length..], plaintext);
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
