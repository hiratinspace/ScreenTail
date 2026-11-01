using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ScreenTail.Api.Auth;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// Google ID tokens made in the test, signed by an RSA key made in the test (ST-099). Nothing here is
/// Google's and nothing here calls Google: the API is handed <see cref="FakeGoogleKeys"/> in place of the
/// published key set, and every key is generated when the suite runs, so no key material is in the
/// repository.
/// </summary>
internal sealed class GoogleTokens : IDisposable
{
    /// <summary>The shape of a Google OAuth client id. Not a secret: the browser is given it too.</summary>
    public const string ClientId = "1234567890-screentail-tests.apps.googleusercontent.com";

    public const string KeyId = "test-key-1";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _other = RSA.Create(2048);

    public GoogleTokens()
    {
        Key = new RsaSecurityKey(_rsa) { KeyId = KeyId };
    }

    /// <summary>The key Google's published set holds, as far as the API is concerned.</summary>
    public RsaSecurityKey Key { get; }

    /// <summary>A key that is not in the published set, under whatever id a test gives it.</summary>
    public RsaSecurityKey Other(string keyId) => new(_other) { KeyId = keyId };

    /// <summary>A good token for a Google account unless a test says otherwise.</summary>
    public string Mint(
        string subject = "google-sub-1",
        string email = "owner@acme.example",
        object? emailVerified = null,
        bool omitEmailVerified = false,
        string audience = ClientId,
        string issuer = "https://accounts.google.com",
        DateTimeOffset? expires = null,
        DateTimeOffset? notBefore = null,
        SecurityKey? signWith = null,
        string algorithm = SecurityAlgorithms.RsaSha256,
        string? name = "Pat Owner")
    {
        var until = expires ?? DateTimeOffset.UtcNow.AddMinutes(30);
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["sub"] = subject,
            ["email"] = email,
            ["azp"] = ClientId,
        };
        if (!omitEmailVerified)
        {
            claims["email_verified"] = emailVerified ?? true;
        }

        if (name is not null)
        {
            claims["name"] = name;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = until.AddHours(-1).UtcDateTime,
            NotBefore = (notBefore ?? until.AddHours(-1)).UtcDateTime,
            Expires = until.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(signWith ?? Key, algorithm),
        });
    }

    /// <summary>The same payload with <c>alg: none</c> and no signature: the oldest JWT break there is.</summary>
    public static string Unsigned(string subject = "google-sub-1", string email = "owner@acme.example")
    {
        static string Part(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));
        var exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        return Part("""{"alg":"none","typ":"JWT"}""") + "."
            + Part($$"""{"iss":"https://accounts.google.com","aud":"{{ClientId}}","sub":"{{subject}}","email":"{{email}}","email_verified":true,"exp":{{exp}}}""") + ".";
    }

    /// <summary>Signed with a symmetric key in place of Google's RSA one: the algorithm-confusion attempt.</summary>
    public string Hmac(string subject = "google-sub-1", string email = "owner@acme.example") =>
        Mint(subject, email, signWith: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = KeyId }, algorithm: SecurityAlgorithms.HmacSha256);

    public void Dispose()
    {
        _rsa.Dispose();
        _other.Dispose();
    }
}

/// <summary>Google's key set as the test says it is. Counts the refreshes asked for; can be made unreachable.</summary>
internal sealed class FakeGoogleKeys : IGoogleSigningKeys
{
    private readonly Lock _gate = new();

    public FakeGoogleKeys(params SecurityKey[] keys)
    {
        Current = [.. keys];
    }

    public IReadOnlyList<SecurityKey> Current { get; set; }

    /// <summary>What a refresh brings, when a test is rotating keys.</summary>
    public IReadOnlyList<SecurityKey>? AfterRefresh { get; set; }

    public bool Unavailable { get; set; }

    public int Refreshes { get; private set; }

    /// <summary>Every time a token was checked against these keys: how a test sees a request reach the verifier.</summary>
    public int Calls { get; private set; }

    public Task<IReadOnlyList<SecurityKey>> GetAsync(bool refresh, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls++;
            if (Unavailable)
            {
                throw new GoogleKeysUnavailableException();
            }

            if (refresh)
            {
                Refreshes++;
                if (AfterRefresh is { } rotated)
                {
                    Current = rotated;
                }
            }

            return Task.FromResult(Current);
        }
    }
}

/// <summary>Put in place of the real key source by every test host, so that no test can reach Google.</summary>
internal sealed class NoGoogle : IGoogleSigningKeys
{
    public Task<IReadOnlyList<SecurityKey>> GetAsync(bool refresh, CancellationToken ct) =>
        throw new InvalidOperationException("The tests never fetch Google's keys.");
}
