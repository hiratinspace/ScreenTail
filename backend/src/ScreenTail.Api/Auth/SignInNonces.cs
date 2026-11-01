using System.Linq.Expressions;
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
/// a token only when that claim names a nonce it issued, that has not expired and has not been used, and
/// marks it used in the same statement that checks it, at the moment a session is started. A token
/// captured after it was used, or one minted for a nonce we never issued, is refused.
///
/// Kept in the database rather than in memory or a cookie: single use has to hold across a restart and
/// across more than one API instance, and a cookie cannot be "used up" in the copy an attacker took.
/// Only the SHA-256 of a nonce is stored, as with invite codes.
/// </summary>
public static class SignInNonces
{
    /// <summary>256 random bits, URL-safe base64 without padding: 43 characters.</summary>
    public const int Length = 43;

    /// <summary>A fresh nonce and the row that records it. Not yet saved.</summary>
    public static (IssuedNonce Issued, AdminSignInNonce Row) Create(TimeSpan lifetime, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = time.GetUtcNow();
        var expires = now + lifetime;
        return (new IssuedNonce(nonce, expires, (int)lifetime.TotalSeconds), new AdminSignInNonce { Id = Hash(nonce), CreatedAt = now, ExpiresAt = expires });
    }

    /// <summary>Issues one, deleting every row past its expiry first so the table holds only live nonces and recently used ones.</summary>
    public static async Task<IssuedNonce> IssueAsync(ScreenTailContext db, TimeSpan lifetime, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        var now = time.GetUtcNow();
        _ = await db.AdminSignInNonces.Where(n => n.ExpiresAt <= now).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var (issued, row) = Create(lifetime, time);
        db.AdminSignInNonces.Add(row);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return issued;
    }

    /// <summary>Whether this nonce was issued here, has not expired and has not been used. Reads only.</summary>
    public static async Task<bool> IsLiveAsync(ScreenTailContext db, string? nonce, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        if (!WellFormed(nonce))
        {
            return false;
        }

        return await db.AdminSignInNonces.AnyAsync(Live(Hash(nonce), time.GetUtcNow()), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the nonce used if, and only if, it is live, in one statement: of two requests carrying the
    /// same token at once, exactly one gets true.
    /// </summary>
    public static async Task<bool> ConsumeAsync(ScreenTailContext db, string? nonce, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        if (!WellFormed(nonce))
        {
            return false;
        }

        var now = time.GetUtcNow();
        var marked = await db.AdminSignInNonces
            .Where(Live(Hash(nonce), now))
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.UsedAt, now), ct)
            .ConfigureAwait(false);
        return marked == 1;
    }

    /// <summary>Issued here, unused and unexpired: the one rule both the check and the use apply.</summary>
    private static Expression<Func<AdminSignInNonce, bool>> Live(string hash, DateTimeOffset now) =>
        n => n.Id == hash && n.UsedAt == null && n.ExpiresAt > now;

    public static string Hash(string nonce) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce)));

    /// <summary>The shape this service issues, and nothing else: anything longer or stranger is refused without a lookup.</summary>
    private static bool WellFormed([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? nonce) =>
        nonce is { Length: Length } && nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
