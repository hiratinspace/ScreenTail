using System.Security.Cryptography;
using System.Text;

namespace ScreenTail.Api.Auth;

/// <summary>
/// How a device proves who it is (ST-008).
///
/// A device is activated once and then holds a long-lived refresh token. The database keeps only the
/// SHA-256 of it: this table is what an attacker with read access would go for, and a stolen device
/// token drives capture on a technician's machine. Comparing hashes means a database dump is not a key
/// ring.
///
/// The token is a 256-bit random value, so there is nothing to guess and no structure to forge. It is
/// never logged, never returned after activation, and never put in a URL.
/// </summary>
public static class DeviceTokens
{
    /// <summary>256 bits. Long enough that brute force is not a threat model, short enough to paste once.</summary>
    public const int Bytes = 32;

    /// <summary>The characters <see cref="Create"/> writes: <see cref="Bytes"/> in unpadded base64url, 43 for 32 bytes.</summary>
    public const int Length = ((Bytes * 8) + 5) / 6;

    public static string Create() => Base64Url(RandomNumberGenerator.GetBytes(Bytes));

    /// <summary>
    /// Whether a presented token could be one <see cref="Create"/> wrote: exactly <see cref="Length"/>
    /// characters of the base64url alphabet. Anything else is refused without hashing or a lookup.
    /// </summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: Length } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Lower-case hex of the SHA-256. What the database stores, and all it stores.</summary>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>
    /// Constant-time comparison, for the paths that compare a presented token against a known one.
    ///
    /// The database lookup is by hash and does not need this, but a future code path that compares two
    /// strings with <c>==</c> leaks their common prefix through timing, and that is the sort of thing
    /// that is invisible in review.
    /// </summary>
    public static bool Matches(string presentedHash, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presentedHash ?? string.Empty),
            Encoding.UTF8.GetBytes(storedHash ?? string.Empty));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
