using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Auth;

/// <summary>
/// A nonce for the page to give Google, and when it stops being accepted, also as seconds from now so
/// the page can draw a fresh button in time without trusting its own clock.
/// </summary>
public sealed record IssuedNonce(string Nonce, DateTimeOffset ExpiresAt, int ExpiresInSeconds);

/// <summary>
/// The Google ID token's nonce (ST-099 hardening, ADR-0009): replay protection for owner sign-in.
///
/// The page asks for a nonce before it draws Google's button and hands it to Google Identity Services,
/// which writes it into the ID token's <c>nonce</c> claim under Google's signature. The backend accepts
/// a token only when that claim is a nonce this service made, that has not expired and has not been
/// used, and records it as used at the moment a session starts or a tenant is made.
///
/// <b>Issuing stores nothing.</b> A nonce is its expiry and 128 random bits, signed with HMAC-SHA256
/// under a key of its own (<see cref="TokenIssuer.NonceKeyFrom"/>), so any instance can check that it
/// made it and when it runs out without a lookup, and the anonymous route that hands nonces out cannot
/// fill a table. <b>Using one stores its hash</b> (the primary key of <c>admin_sign_in_nonces</c>), in
/// the same save as the session's or the tenant's own write: a second use is a key conflict, so of two
/// requests carrying the same token exactly one gets through, on any number of instances. Rows grow only
/// with real sign-ins, and each use deletes the rows already past their expiry.
/// </summary>
public sealed class SignInNonces(JwtOptions jwt, AdminOptions options, TimeProvider time)
{
    private const byte Version = 1;

    private const int ExpiryBytes = 8;

    private const int RandomBytes = 16;

    private const int MacBytes = 32;

    private const int SignedBytes = 1 + ExpiryBytes + RandomBytes;

    /// <summary>The nonce's length as text: 57 bytes, URL-safe base64 without padding.</summary>
    public const int Length = 76;

    private readonly byte[] _key = TokenIssuer.NonceKeyFrom(jwt.SigningKey);

    /// <summary>A fresh nonce, good for <see cref="AdminOptions.SignInNonceLifetime"/>. Writes nothing anywhere.</summary>
    public IssuedNonce Issue()
    {
        var lifetime = options.SignInNonceLifetime;
        var expires = DateTimeOffset.FromUnixTimeSeconds((time.GetUtcNow() + lifetime).ToUnixTimeSeconds());
        return new IssuedNonce(Mint(_key, expires), expires, (int)lifetime.TotalSeconds);
    }

    /// <summary>A nonce under any key and expiry. The service uses its own; tests make ones it must refuse.</summary>
    internal static string Mint(byte[] key, DateTimeOffset expires)
    {
        var bytes = new byte[SignedBytes + MacBytes];
        bytes[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1, ExpiryBytes), expires.ToUnixTimeSeconds());
        RandomNumberGenerator.Fill(bytes.AsSpan(1 + ExpiryBytes, RandomBytes));
        _ = HMACSHA256.HashData(key, bytes.AsSpan(0, SignedBytes), bytes.AsSpan(SignedBytes));
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Whether this service made the nonce (its MAC, compared in constant time) and it has not expired,
    /// with no expiry further away than one lifetime. Reads nothing.
    /// </summary>
    internal bool TryRead(string? nonce, out DateTimeOffset expires)
    {
        expires = default;
        if (nonce is not { Length: Length } || !nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        var bytes = Convert.FromBase64String(nonce.Replace('-', '+').Replace('_', '/'));
        if (bytes.Length != SignedBytes + MacBytes || bytes[0] != Version)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[MacBytes];
        _ = HMACSHA256.HashData(_key, bytes.AsSpan(0, SignedBytes), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(SignedBytes)))
        {
            return false;
        }

        var seconds = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, ExpiryBytes));
        if (seconds < 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var now = time.GetUtcNow();
        return now < expires && expires <= now + options.SignInNonceLifetime + TimeSpan.FromSeconds(1);
    }

    /// <summary>Made here, unexpired, and not used yet. Reads only.</summary>
    public async Task<bool> IsLiveAsync(ScreenTailContext db, string? nonce, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!TryRead(nonce, out _))
        {
            return false;
        }

        var hash = Hash(nonce!);
        return !await db.AdminSignInNonces.AnyAsync(n => n.Id == hash, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the record that this nonce is used to <paramref name="db"/>, for the caller's own save to
    /// write with whatever else the sign-in writes; false, adding nothing, for a nonce that is not good.
    /// Deletes the records already past their expiry first. The save fails with a key conflict when
    /// another request used the nonce first; <see cref="WasUsedAsync"/> tells that apart from any other
    /// failure.
    /// </summary>
    public async Task<bool> MarkUsedAsync(ScreenTailContext db, string? nonce, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!TryRead(nonce, out var expires))
        {
            return false;
        }

        var now = time.GetUtcNow();
        _ = await db.AdminSignInNonces.Where(n => n.ExpiresAt <= now).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.AdminSignInNonces.Add(new AdminSignInNonce { Id = Hash(nonce!), ExpiresAt = expires, UsedAt = now });
        return true;
    }

    /// <summary>Whether a record of this nonce's use is in the database: after a failed save, the conflict was a second use.</summary>
    public static async Task<bool> WasUsedAsync(ScreenTailContext db, string? nonce, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrEmpty(nonce))
        {
            return false;
        }

        var hash = Hash(nonce);
        return await db.AdminSignInNonces.AsNoTracking().AnyAsync(n => n.Id == hash, ct).ConfigureAwait(false);
    }

    public static string Hash(string nonce) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));
}
