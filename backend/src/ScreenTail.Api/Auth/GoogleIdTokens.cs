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

        // Lower-cased in ASCII only. Lowering the rest folds look-alikes into ASCII (the Kelvin sign
        // becomes a plain k), and an address that is not ASCII is refused for signup anyway.
        var address = AsciiLower(email.Trim());
        var name = identity.FindFirst("name")?.Value;
        return new GoogleIdentity(subject, address, Truncate(string.IsNullOrWhiteSpace(name) ? address : name.Trim(), DisplayNameLength));
    }

    /// <summary>The owners table's display-name column. A name, or the address standing in for one, is cut to fit.</summary>
    public const int DisplayNameLength = 200;

    private static string AsciiLower(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
            }
        });

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
/// token naming a key that is not cached asks for a fresh copy, at most once every five minutes counted
/// from the last attempt whether it worked or failed, so a stream of made-up key ids cannot turn into a
/// stream of requests to Google. While a fetch is under way, a caller with a fresh copy to hand is
/// answered from it rather than queued.
///
/// <b>Time-bounded:</b> ten seconds for the whole fetch, and 64 KB for the answer. A fetch that fails
/// keeps a copy that is still fresh. With no fresh copy it fails closed: sign-in is refused until Google
/// answers, rather than checked against keys that may since have been withdrawn, and Google is asked
/// again no sooner than thirty seconds after the failure.
/// </summary>
public sealed partial class GoogleSigningKeys(Func<HttpClient> client, TimeProvider time, ILogger<GoogleSigningKeys>? logger = null) : IGoogleSigningKeys, IDisposable
{
    private readonly ILogger _log = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>
    /// Says that a fetch failed and what kind of failure it was, and nothing else: no key, no body, no
    /// address beyond the one fixed host (INV-10). Sign-in is refused while this keeps happening.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Google's signing keys could not be fetched ({Failure}); a fresh cached copy is in use: {CopyInUse}.")]
    private static partial void FetchFailed(ILogger logger, string failure, bool copyInUse);

    public const string HttpClientName = nameof(GoogleSigningKeys);

    public static readonly Uri Address = new("https://www.googleapis.com/oauth2/v3/certs");

    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan ShortestCache = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan LongestCache = TimeSpan.FromHours(24);

    /// <summary>When Google's answer carries no max-age.</summary>
    public static readonly TimeSpan DefaultCache = TimeSpan.FromHours(1);

    /// <summary>The least time between two fetches asked for by an unknown key id, counted from the last attempt, whether it worked or not.</summary>
    public static readonly TimeSpan RefreshFloor = TimeSpan.FromMinutes(5);

    /// <summary>With no usable copy, the least time between a failed fetch and the next one. Sign-in waits that long after Google fails.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

    private const int MaximumBytes = 64 * 1024;

    private readonly SemaphoreSlim _gate = new(1, 1);

    // One immutable snapshot, swapped whole, so a caller that does not take the gate never reads half of
    // an update.
    private volatile Snapshot _state = new([], DateTimeOffset.MinValue, DateTimeOffset.MinValue, LastFailed: false);

    private int _fetches;

    /// <summary>How many times Google was asked. For the tests that prove the cache holds.</summary>
    public int Fetches => Volatile.Read(ref _fetches);

    public async Task<IReadOnlyList<SecurityKey>> GetAsync(bool refresh, CancellationToken ct)
    {
        var state = _state;
        var now = time.GetUtcNow();
        if (Serves(state, now, refresh))
        {
            return state.Keys;
        }

        // A fetch already under way and a copy that is still fresh: answer with the copy rather than
        // queue behind somebody else's request to Google.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            if (now < state.FreshUntil)
            {
                return state.Keys;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            state = _state;
            now = time.GetUtcNow();
            if (Serves(state, now, refresh))
            {
                return state.Keys;
            }

            // Nothing usable, and Google failed a moment ago: refuse without asking again, so a stream of
            // sign-ins while Google is down is not a stream of requests to it.
            if (now >= state.FreshUntil && state.LastFailed && now - state.LastAttempt < RetryAfterFailure)
            {
                throw new GoogleKeysUnavailableException();
            }

            // The attempt counts from here, whatever comes of it: the floor holds on the failure path too.
            _state = state with { LastAttempt = now };
            try
            {
                var (keys, lifetime) = await FetchAsync(ct).ConfigureAwait(false);
                _state = new Snapshot(keys, now + lifetime, now, LastFailed: false);
                return keys;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or ArgumentException or InvalidOperationException or GoogleKeysUnavailableException
                || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                _state = state with { LastAttempt = now, LastFailed = true };
                FetchFailed(_log, e is OperationCanceledException ? "timed out" : e.GetType().Name, now < state.FreshUntil);
                if (now < state.FreshUntil)
                {
                    return state.Keys;
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
    /// last attempt to fetch, good or failed, was too recent for another one.
    /// </summary>
    private static bool Serves(Snapshot state, DateTimeOffset now, bool refresh) =>
        now < state.FreshUntil && (!refresh || now - state.LastAttempt < RefreshFloor);

    private sealed record Snapshot(IReadOnlyList<SecurityKey> Keys, DateTimeOffset FreshUntil, DateTimeOffset LastAttempt, bool LastFailed);

    private async Task<(IReadOnlyList<SecurityKey> Keys, TimeSpan Lifetime)> FetchAsync(CancellationToken ct)
    {
        _ = Interlocked.Increment(ref _fetches);
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
