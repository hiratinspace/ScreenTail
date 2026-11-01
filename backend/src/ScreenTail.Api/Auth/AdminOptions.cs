namespace ScreenTail.Api.Auth;

/// <summary>
/// The web admin's configuration (ST-099, ADR-0009). Every default is the closed one.
///
/// <b>No Google client id, no sign-in.</b> There is no default id, the way there is no default signing
/// key: without one the sign-in endpoints answer 501 and say so, and nothing else changes. No client
/// secret exists anywhere here, because checking a Google ID token needs only Google's public keys and
/// the id the token was issued for.
///
/// <b>An empty allowlist means nobody can sign up.</b> Signup creates a tenant, and who may create one is
/// the operator's decision, not the internet's. An owner who already exists signs in whatever the list
/// says, so taking an address off it later does not lock its tenant out.
/// </summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>The OAuth client id from Google Cloud Console. Not a secret; also given to the web app as VITE_GOOGLE_CLIENT_ID.</summary>
    public string GoogleClientId { get; set; } = string.Empty;

    /// <summary>The Google addresses that may create a tenant by signing in for the first time. Compared without regard to case.</summary>
    public IList<string> SignupAllowlist { get; } = [];

    /// <summary>Seats a tenant created by signup starts with. Five, like the operator's <c>--invite new:</c>.</summary>
    public int NewTenantSeats { get; set; } = 5;

    /// <summary>How long an admin session lasts before the owner signs in again. There is no refresh.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Where technicians download the client. Empty until there is a published installer (ST-112, ST-007).</summary>
    public string ClientDownloadUrl { get; set; } = string.Empty;

    public bool SignInConfigured => !string.IsNullOrWhiteSpace(GoogleClientId);

    /// <summary>Whether this address may create a tenant. Only a verified address is ever asked about.</summary>
    public bool MaySignUp(string email) =>
        !string.IsNullOrWhiteSpace(email)
        && SignupAllowlist.Any(allowed => string.Equals(allowed?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The download link, when it is an absolute https address; otherwise none.</summary>
    public Uri? DownloadUri() =>
        Uri.TryCreate(ClientDownloadUrl?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    /// <summary>Refuses, at startup, a setting that would do something other than what it says.</summary>
    public void Validate(JwtOptions jwt)
    {
        ArgumentNullException.ThrowIfNull(jwt);
        if (NewTenantSeats is < 1 or > 1000)
        {
            throw new InvalidOperationException("Admin:NewTenantSeats must be between 1 and 1000.");
        }

        if (SessionLifetime < TimeSpan.FromMinutes(5) || SessionLifetime > TimeSpan.FromHours(24))
        {
            throw new InvalidOperationException("Admin:SessionLifetime must be between 5 minutes and 24 hours.");
        }

        if (!string.IsNullOrWhiteSpace(ClientDownloadUrl) && DownloadUri() is null)
        {
            throw new InvalidOperationException("Admin:ClientDownloadUrl must be an absolute https address, or empty.");
        }

        // A device token and an admin session are told apart by their audience first of all. Configured
        // to the same value, a device token would pass the admin scheme's signature and audience checks.
        if (string.Equals(jwt.Audience, AdminSession.Audience, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Jwt:Audience must not be '{AdminSession.Audience}', which is the admin session's audience.");
        }
    }
}
