using Microsoft.IdentityModel.Tokens;

namespace ScreenTail.Api.Auth;

/// <summary>
/// A stand-in for Google's published key set, for the web admin's browser tests (ST-099 follow-up,
/// ADR-0009 "Testing in a real browser"): <c>Testing:GoogleSigningKeys</c> holds a JSON Web Key Set of
/// public RSA keys, and while it is set Google ID tokens are checked against those keys in place of
/// Google's. Every other check stays as it is: RS256 only, the key the token names, Google's issuer, the
/// configured client id as the audience, an unexpired token, a verified address and a single-use nonce.
///
/// <b>Development only, and refused anywhere else at startup.</b> Whoever holds the private half of a key
/// in this set can sign in as any Google account on the allowlist, so a host that is not Development does
/// not start with the setting at all, and the composition root registers the stand-in only in Development
/// besides. The browser tests make a fresh key pair for each run and hand this host the public half; no
/// key is in the repository, and a set carrying a private key is refused, so the setting is never the
/// place a signing key is kept.
/// </summary>
public sealed partial class TestSignInOptions
{
    public const string Section = "Testing";

    /// <summary>The setting's full name, for messages and documentation.</summary>
    public const string Setting = Section + ":" + nameof(GoogleSigningKeys);

    /// <summary>Far larger than a set of a few RSA public keys. A larger value is not parsed.</summary>
    private const int MaximumLength = 64 * 1024;

    /// <summary>A JSON Web Key Set (<c>{"keys":[...]}</c>) of public RSA keys, each with a key id. Empty: Google's keys.</summary>
    public string GoogleSigningKeys { get; set; } = string.Empty;

    public bool Configured => !string.IsNullOrWhiteSpace(GoogleSigningKeys);

    /// <summary>Throws when the setting is present anywhere but Development, or is not a usable public key set.</summary>
    public void Validate(bool isDevelopment)
    {
        if (!Configured)
        {
            return;
        }

        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                $"{Setting} is set outside Development. It replaces Google's signing keys with keys whose private "
                + "half somebody else holds, so anyone with that half could sign in as an owner. The service refuses "
                + "to start with it. Remove the setting.");
        }

        _ = Keys();
    }

    /// <summary>The keys, each a public RSA key with an id.</summary>
    /// <exception cref="InvalidOperationException">The value is not a key set this stand-in accepts.</exception>
    public IReadOnlyList<SecurityKey> Keys()
    {
        if (GoogleSigningKeys.Length > MaximumLength)
        {
            throw new InvalidOperationException($"{Setting} is longer than {MaximumLength} characters.");
        }

        JsonWebKeySet set;
        try
        {
            set = new JsonWebKeySet(GoogleSigningKeys);
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException($"{Setting} is not a JSON Web Key Set.", e);
        }

        if (set.Keys.Count == 0)
        {
            throw new InvalidOperationException($"{Setting} holds no key.");
        }

        foreach (var key in set.Keys)
        {
            if (key.Kty != JsonWebAlgorithmsKeyTypes.RSA || string.IsNullOrWhiteSpace(key.Kid)
                || string.IsNullOrEmpty(key.N) || string.IsNullOrEmpty(key.E))
            {
                throw new InvalidOperationException($"{Setting} must hold RSA public keys, each with a key id.");
            }

            // Only the public half belongs here. A private one would make this setting a place where a
            // key that signs owners in is kept, copied and logged.
            if (key.HasPrivateKey || !string.IsNullOrEmpty(key.D) || !string.IsNullOrEmpty(key.P) || !string.IsNullOrEmpty(key.Q))
            {
                throw new InvalidOperationException($"{Setting} holds a private key. Give it the public half only.");
            }
        }

        return [.. set.Keys.Cast<SecurityKey>()];
    }

    /// <summary>Said once at startup, so nobody reads a log of this host and takes its sign-ins for Google's.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Google's signing keys are replaced by Testing:GoogleSigningKeys (Development only): sign-in accepts Google ID tokens signed by the keys in that setting.")]
    public static partial void Announce(ILogger logger);
}

/// <summary>The key set <see cref="TestSignInOptions"/> names, served as Google's would be. Never fetches anything.</summary>
public sealed class TestGoogleSigningKeys(IReadOnlyList<SecurityKey> keys) : IGoogleSigningKeys
{
    public IReadOnlyList<SecurityKey> Keys { get; } = keys;

    public Task<IReadOnlyList<SecurityKey>> GetAsync(bool refresh, CancellationToken ct) => Task.FromResult(Keys);
}
