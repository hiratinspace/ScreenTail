using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ScreenTail.Api.Auth;

/// <summary>Who Google says signed in: the account's stable id, its verified address and the name it shows.</summary>
public sealed record GoogleIdentity(string Subject, string Email, string Name);

/// <summary>Google's published signing keys, behind a seam so that no test ever calls Google.</summary>
public interface IGoogleSigningKeys
{
    /// <summary>
    /// The keys to check a token against. <paramref name="refresh"/> asks for a fresh copy because a
    /// token named a key that is not in this one; the source decides whether it is allowed to fetch.
    /// </summary>
    /// <exception cref="GoogleKeysUnavailableException">There are no usable keys and none could be fetched.</exception>
    Task<IReadOnlyList<SecurityKey>> GetAsync(bool refresh, CancellationToken ct);
}

/// <summary>No usable copy of Google's keys: the sign-in cannot be checked, so it is refused.</summary>
public sealed class GoogleKeysUnavailableException : Exception
{
    public GoogleKeysUnavailableException()
        : base("Google's signing keys could not be fetched and no fresh copy is cached.")
    {
    }

    public GoogleKeysUnavailableException(string message)
        : base(message)
    {
    }

    public GoogleKeysUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Checks a Google ID token from Google Identity Services in the browser (ST-099, ADR-0009).
///
/// Every check is ours, none is the token's: the signature against Google's published RSA keys and
/// only RS256, the issuer one of Google's two spellings, the audience this deployment's client id and
/// nothing else, an expiry that has not passed with no grace, and an address Google says it verified.
/// A token that fails any of them is null, with no reason given, because the reason helps only the
/// person forging it.
/// </summary>
public sealed class GoogleIdTokenVerifier(IGoogleSigningKeys keys, AdminOptions options, TimeProvider time)
{
    /// <summary>The issuers Google documents for ID tokens. Either may appear.</summary>
    public static readonly IReadOnlyList<string> Issuers = ["accounts.google.com", "https://accounts.google.com"];

    /// <summary>Far longer than any Google ID token. A larger one is not parsed at all.</summary>
    private const int MaximumLength = 8 * 1024;

    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    /// <summary>The identity, or null for a token that is not a good Google sign-in for this deployment.</summary>
    /// <exception cref="GoogleKeysUnavailableException">Google's keys cannot be had, so nothing can be checked.</exception>
    public async Task<GoogleIdentity?> VerifyAsync(string? idToken, CancellationToken ct)
    {
        if (!options.SignInConfigured || string.IsNullOrWhiteSpace(idToken) || idToken.Length > MaximumLength)
        {
            return null;
        }

        var result = await ValidateAsync(idToken, await keys.GetAsync(refresh: false, ct).ConfigureAwait(false)).ConfigureAwait(false);

        // Google rotates its keys, so a token signed with a key this copy has not seen yet is asked about
        // once more against a fresh copy. The key source decides how often that may really fetch.
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            result = await ValidateAsync(idToken, await keys.GetAsync(refresh: true, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        if (!result.IsValid || result.ClaimsIdentity is not { } identity)
        {
            return null;
        }

        // Google says boolean true; an older token said the string "true". Anything else, absent
        // included, is an address nobody proved belongs to the person signing in.
        if (!string.Equals(identity.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var subject = identity.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var email = identity.FindFirst(JwtRegisteredClaimNames.Email)?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 255 || string.IsNullOrWhiteSpace(email) || email.Length > 320)
        {
            return null;
        }

        var name = identity.FindFirst("name")?.Value;
        return new GoogleIdentity(subject, email.Trim().ToLowerInvariant(), string.IsNullOrWhiteSpace(name) ? email.Trim() : Truncate(name.Trim(), 200));
    }

    /// <summary>The rules, all on. Exposed so a test can see that none was switched off.</summary>
    public TokenValidationParameters Parameters(IReadOnlyList<SecurityKey> signingKeys) => new()
    {
        ValidateIssuer = true,
        ValidIssuers = Issuers,
        ValidateAudience = true,
        ValidAudience = options.GoogleClientId.Trim(),
        ValidateLifetime = true,
        RequireExpirationTime = true,
        LifetimeValidator = NotExpired,
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        IssuerSigningKeys = signingKeys,

        // The key the token names, or none: trying every key would hide a token signed by something
        // that is not in Google's set behind a generic signature failure, and skip the refresh above.
        TryAllIssuerSigningKeys = false,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ClockSkew = TimeSpan.Zero,
    };

    private Task<TokenValidationResult> ValidateAsync(string idToken, IReadOnlyList<SecurityKey> signingKeys) =>
        _handler.ValidateTokenAsync(idToken, Parameters(signingKeys));

    /// <summary>Expiry against this service's clock, so a test can hold it still. No grace either way.</summary>
    private bool NotExpired(DateTime? notBefore, DateTime? expires, SecurityToken token, TokenValidationParameters parameters)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return expires is { } until && now < until && (notBefore is not { } from || now >= from);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

/// <summary>
/// Google's published keys, fetched from the one address Google documents and cached (ST-099).
///
/// <b>Cached</b> for as long as Google's Cache-Control says, held between five minutes and a day. A
/// token naming a key that is not cached asks for a fresh copy, at most once every five minutes, so a
/// stream of made-up key ids cannot turn into a stream of requests to Google.
///
/// <b>Time-bounded:</b> ten seconds for the whole fetch, and 64 KB for the answer. A fetch that fails
/// keeps a copy that is still fresh. With no fresh copy it fails closed: sign-in is refused until Google
/// answers, rather than checked against keys that may since have been withdrawn.
/// </summary>
public sealed class GoogleSigningKeys(Func<HttpClient> client, TimeProvider time) : IGoogleSigningKeys, IDisposable
{
    public const string HttpClientName = nameof(GoogleSigningKeys);

    public static readonly Uri Address = new("https://www.googleapis.com/oauth2/v3/certs");

    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan ShortestCache = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan LongestCache = TimeSpan.FromHours(24);

    /// <summary>When Google's answer carries no max-age.</summary>
    public static readonly TimeSpan DefaultCache = TimeSpan.FromHours(1);

    /// <summary>The least time between two fetches asked for by an unknown key id.</summary>
    public static readonly TimeSpan RefreshFloor = TimeSpan.FromMinutes(5);

    private const int MaximumBytes = 64 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SecurityKey> _keys = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _freshUntil = DateTimeOffset.MinValue;

    /// <summary>How many times Google was asked. For the tests that prove the cache holds.</summary>
    public int Fetches { get; private set; }

    public async Task<IReadOnlyList<SecurityKey>> GetAsync(bool refresh, CancellationToken ct)
    {
        if (Serves(time.GetUtcNow(), refresh))
        {
            return _keys;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            if (Serves(now, refresh))
            {
                return _keys;
            }

            try
            {
                var (keys, lifetime) = await FetchAsync(ct).ConfigureAwait(false);
                _keys = keys;
                _fetchedAt = now;
                _freshUntil = now + lifetime;
                return keys;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or ArgumentException or InvalidOperationException or GoogleKeysUnavailableException
                || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                if (now < _freshUntil)
                {
                    return _keys;
                }

                throw new GoogleKeysUnavailableException("Google's signing keys could not be fetched and no fresh copy is cached.", e);
            }
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Whether the cached copy answers this ask: it is fresh, and either nobody asked for a refresh or the
    /// last fetch was too recent for another one.
    /// </summary>
    private bool Serves(DateTimeOffset now, bool refresh) =>
        now < _freshUntil && (!refresh || now - _fetchedAt < RefreshFloor);

    private async Task<(IReadOnlyList<SecurityKey> Keys, TimeSpan Lifetime)> FetchAsync(CancellationToken ct)
    {
        Fetches++;
        using var deadline = new CancellationTokenSource(FetchTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var http = client();
        using var response = await http.GetAsync(Address, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumBytes)
        {
            throw new GoogleKeysUnavailableException("Google's key set was larger than expected.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumBytes)
            {
                throw new GoogleKeysUnavailableException("Google's key set was larger than expected.");
            }

            buffer.Write(chunk, 0, read);
        }

        var set = new JsonWebKeySet(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        var keys = set.Keys
            .Where(k => k.Kty == JsonWebAlgorithmsKeyTypes.RSA && !string.IsNullOrEmpty(k.Kid))
            .Cast<SecurityKey>()
            .ToList();
        if (keys.Count == 0)
        {
            throw new GoogleKeysUnavailableException("Google's key set held no RSA key.");
        }

        var maxAge = response.Headers.CacheControl?.MaxAge ?? DefaultCache;
        var lifetime = maxAge < ShortestCache ? ShortestCache : maxAge > LongestCache ? LongestCache : maxAge;
        return (keys, lifetime);
    }
}
