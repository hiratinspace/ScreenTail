using System.Security.Cryptography;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Tenancy;

/// <param name="Code">The one copy of the code in plain text: it is shown to the admin once and stored hashed.</param>
public sealed record IssuedInvite(Guid Id, string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Invites (ST-010 AC1): a code an admin gives a technician, good for 72 hours and one device.
///
/// <b>Twenty characters</b> from an alphabet with no look-alikes (no 0/O, 1/I/L), each drawn uniformly,
/// written <c>XXXXX-XXXXX-XXXXX-XXXXX</c> so it survives being read out over the phone: 20 x log2(31),
/// about 99 bits. That is what makes guessing infeasible however many addresses guess at once, with no
/// lockout anybody could turn against a real technician (ADR-0009, "Guessing invite codes"). Codes made
/// before 2026-11-01 were ten characters (about 49 bits); they are still accepted until they expire,
/// 72 hours at most, because a code is looked up by its hash whatever its length.
///
/// Stored as a hash, like a device token: the database never holds a code somebody could use, and at
/// 99 bits a copy of the table cannot be searched for one either.
///
/// No email is sent. There is no mail provider yet (ST-007 decides hosting, and mail with it), so the
/// operator hands the code on; when there is one, the sending is a line in the CLI below.
/// </summary>
public static class Invites
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>31 characters: the digits 2 to 9 and the capital letters but I, L and O.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>Characters in a code made today, without the dashes.</summary>
    public const int CodeLength = 20;

    /// <summary>Characters in a code made before 2026-11-01, still accepted until it expires.</summary>
    public const int LegacyCodeLength = 10;

    /// <summary>Characters between dashes, for reading out.</summary>
    public const int GroupLength = 5;

    /// <summary>Bits of entropy in a code made today: <see cref="CodeLength"/> x log2(31).</summary>
    public static double EntropyBits => CodeLength * Math.Log2(Alphabet.Length);

    public static async Task<IssuedInvite> CreateAsync(ScreenTailContext db, Guid tenantId, string email, string displayName, TimeProvider time, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var code = NewCode();
        var now = time.GetUtcNow();
        var invite = new Invite
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Email = email.Trim(),
            DisplayName = displayName.Trim(),
            CodeHash = Hash(code),
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        db.Invites.Add(invite);
        _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new IssuedInvite(invite.Id, code, invite.ExpiresAt);
    }

    /// <summary>The hash a code is stored and looked up by, after the dashes, spaces and case a person adds are taken off.</summary>
    public static string Hash(string code) => DeviceTokens.Hash(Normalise(code));

    public static string Normalise(string? code) =>
        new((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// Whether a normalised code could be one we issued: today's length or the old one, and only
    /// characters of the alphabet. Anything else is refused without asking the database, with the same
    /// answer as a code nobody issued.
    /// </summary>
    public static bool IsWellFormed(string? normalised) =>
        normalised is { Length: CodeLength or LegacyCodeLength } && normalised.All(c => Alphabet.Contains(c, StringComparison.Ordinal));

    /// <summary>
    /// A new code, grouped. <see cref="RandomNumberGenerator.GetString"/> picks each character uniformly:
    /// it rejects the random values past the largest multiple of 31 rather than folding them back with a
    /// remainder, which made the first eight letters of the alphabet one eighth likelier than the rest
    /// (9/256 against 8/256).
    /// </summary>
    internal static string NewCode() => Format(RandomNumberGenerator.GetString(Alphabet, CodeLength));

    /// <summary>A normalised code in groups of five with dashes between, as it is shown and read out.</summary>
    public static string Format(string normalised)
    {
        ArgumentNullException.ThrowIfNull(normalised);
        return string.Join('-', normalised.Chunk(GroupLength).Select(group => new string(group)));
    }
}
