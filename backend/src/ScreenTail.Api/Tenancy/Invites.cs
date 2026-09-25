using System.Security.Cryptography;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Tenancy;

/// <param name="Code">The one copy of the code in plain text: it is shown to the admin once and stored hashed.</param>
public sealed record IssuedInvite(Guid Id, string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// Invites (ST-010 AC1): a code an admin gives a technician, good for 72 hours and one device.
///
/// Ten characters from an alphabet with no look-alikes (no 0/O, 1/I/L), written <c>XXXXX-XXXXX</c> so it
/// survives being read out over the phone; fifty bits, so guessing is not a route in. Stored as a hash,
/// like a device token: the database never holds a code somebody could use.
///
/// No email is sent. There is no mail provider yet (ST-007 decides hosting, and mail with it), so the
/// operator hands the code on; when there is one, the sending is a line in the CLI below.
/// </summary>
public static class Invites
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

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

    private static string NewCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(10);
        var chars = bytes.Select(b => Alphabet[b % Alphabet.Length]).ToArray();
        return $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
    }
}
