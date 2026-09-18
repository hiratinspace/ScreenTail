using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ScreenTail.Api.Auth;

/// <summary>
/// How the API is configured to trust a token (ST-008).
///
/// There is no default signing key, and that is deliberate. A development default is a production key
/// the day somebody forgets to set one, and the failure is silent — everything works, and anyone who
/// has read the source can mint a token for any tenant. The service refuses to start without one.
/// </summary>
public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "screentail";

    public string Audience { get; set; } = "screentail-api";

    /// <summary>At least 32 bytes. Read from configuration; there is no fallback.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// How long an access token lasts. Short, because a device holds a long-lived refresh token and
    /// exchanges it: a leaked access token is then worth minutes rather than months.
    /// </summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Claims this service issues and reads. Names kept short; none of them is personal data.</summary>
public static class ScreenTailClaims
{
    public const string TenantId = "tid";

    public const string UserId = "uid";

    public const string DeviceId = "did";
}

/// <summary>Mints and describes access tokens (ST-008).</summary>
public sealed class TokenIssuer(JwtOptions options)
{
    /// <summary>
    /// An access token for one device.
    ///
    /// The subject is the device, and tenant and user ride along as claims so that every query can be
    /// scoped without another database round trip. No email, no display name, no machine name: a token
    /// is logged by proxies and stored in client memory, and none of that belongs there (INV-10).
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt) ForDevice(Guid tenantId, Guid userId, Guid deviceId, TimeProvider? time = null)
    {
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var expires = now + options.AccessTokenLifetime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, deviceId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(ScreenTailClaims.TenantId, tenantId.ToString()),
                new Claim(ScreenTailClaims.UserId, userId.ToString()),
                new Claim(ScreenTailClaims.DeviceId, deviceId.ToString()),
            ]),
            SigningCredentials = new SigningCredentials(KeyFrom(options.SigningKey), SecurityAlgorithms.HmacSha256),
        };

        return (new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor), expires);
    }

    /// <summary>
    /// The rules a token must satisfy, all of them on.
    ///
    /// The algorithm is pinned to HS256 rather than left to the token. A token that names its own
    /// algorithm is a token that can name <c>none</c>, and every one of the classic JWT breaks comes
    /// from trusting that header.
    /// </summary>
    public static TokenValidationParameters Validation(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = KeyFrom(options.SigningKey),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

            // No grace on expiry. The default is five minutes, which is a long time for a token somebody
            // has taken the trouble to steal.
            ClockSkew = TimeSpan.Zero,
        };
    }

    private static SymmetricSecurityKey KeyFrom(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
        {
            // Refused at startup rather than at the first request, and refused for being short as well as
            // for being missing: a sixteen-character key signs tokens perfectly well and is guessable.
            throw new InvalidOperationException(
                "Jwt:SigningKey must be set to at least 32 bytes. There is no development default, because a "
                + "development default becomes a production key the first time somebody forgets to set one.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }
}
