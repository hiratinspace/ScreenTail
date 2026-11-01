using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Endpoints;

/// <param name="Credential">The ID token Google Identity Services handed the page.</param>
public sealed record AdminSignInRequest(string? Credential);

/// <param name="TenantName">The organisation's name, from the first-run screen.</param>
public sealed record AdminSignupRequest(string? Credential, string? TenantName);

public sealed record AdminOwnerInfo(string Email, string DisplayName);

public sealed record AdminTenantInfo(Guid Id, string Name, int Seats);

/// <summary>
/// What a sign-in came to. <c>signed_in</c> carries the session's CSRF value, which the page holds in
/// memory and sends with every change; <c>needs_tenant</c> means this Google account may create a tenant
/// and has not yet, so the page asks for its name.
/// </summary>
public sealed record AdminSessionResponse(
    string Status,
    string? Csrf = null,
    DateTimeOffset? ExpiresAt = null,
    AdminOwnerInfo? Owner = null,
    AdminTenantInfo? Tenant = null,
    string? Email = null)
{
    public const string SignedIn = "signed_in";

    public const string NeedsTenant = "needs_tenant";

    public static AdminSessionResponse For(AdminCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return new(SignedIn, caller.Csrf, caller.ExpiresAt, new AdminOwnerInfo(caller.Owner.Email, caller.Owner.DisplayName), new AdminTenantInfo(caller.Tenant.Id, caller.Tenant.Name, caller.Tenant.Seats));
    }
}

/// <summary>
/// The owner's sign-in and signup with Google (ST-099, ADR-0009). Anonymous by nature: these are the
/// calls that make a session. Every refusal is a sentence a person can act on, and none of them says
/// which check a token failed.
/// </summary>
public static class AdminSessionEndpoint
{
    public static RouteGroupBuilder MapAdminSession(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/session", async (AdminSignInRequest request, HttpContext http, GoogleIdTokenVerifier google, AdminOptions options, ScreenTailContext db, TokenIssuer issuer, TimeProvider time, CancellationToken ct) =>
        {
            var (identity, refusal) = await VerifyAsync(request.Credential, google, options, ct).ConfigureAwait(false);
            if (identity is null)
            {
                return refusal;
            }

            var owner = await db.Owners.Include(o => o.Tenant).SingleOrDefaultAsync(o => o.GoogleSubject == identity.Subject, ct).ConfigureAwait(false);
            if (owner is null)
            {
                // An owner who exists signs in whatever the list says; only creating a tenant needs it.
                return options.MaySignUp(identity.Email)
                    ? Results.Ok(new AdminSessionResponse(AdminSessionResponse.NeedsTenant, Email: identity.Email))
                    : NotAllowed();
            }

            if (owner.DisabledAt is not null || owner.Tenant is null || owner.Tenant.DisabledAt is not null)
            {
                return Results.Json(new { error = "owner_disabled", message = "This organisation's ScreenTail admin is switched off. Contact ScreenTail support." }, statusCode: StatusCodes.Status403Forbidden);
            }

            owner.LastSignInAt = time.GetUtcNow();
            _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Ok(StartSession(http, issuer, options, owner, owner.Tenant, time));
        })
        .WithName("AdminSignIn")
        .WithSummary("Signs a tenant's owner in with a Google ID token, or says the account may create a tenant.");

        group.MapPost("/signup", async (AdminSignupRequest request, HttpContext http, GoogleIdTokenVerifier google, AdminOptions options, ScreenTailContext db, TokenIssuer issuer, TimeProvider time, CancellationToken ct) =>
        {
            var (identity, refusal) = await VerifyAsync(request.Credential, google, options, ct).ConfigureAwait(false);
            if (identity is null)
            {
                return refusal;
            }

            if (!options.MaySignUp(identity.Email))
            {
                return NotAllowed();
            }

            var name = request.TenantName?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 200)
            {
                return Results.BadRequest(new { error = "bad_tenant_name", message = "Enter your organisation's name, up to 200 characters." });
            }

            if (await db.Owners.AnyAsync(o => o.GoogleSubject == identity.Subject, ct).ConfigureAwait(false))
            {
                return AlreadySignedUp();
            }

            var now = time.GetUtcNow();
            var tenant = new Tenant { Id = Guid.NewGuid(), Name = name, Seats = options.NewTenantSeats, CreatedAt = now };
            var owner = new Owner
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                GoogleSubject = identity.Subject,
                Email = identity.Email,
                DisplayName = identity.Name,
                CreatedAt = now,
                LastSignInAt = now,
            };
            db.Tenants.Add(tenant);
            db.Owners.Add(owner);
            try
            {
                _ = await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Two signups with the same account at once: the unique index let one through, and the
                // other finds that account's owner row now. Any other failure is not this one, and is
                // not answered as if it were: it goes on as the error it is (a 500).
                db.ChangeTracker.Clear();
                if (await db.Owners.AsNoTracking().AnyAsync(o => o.GoogleSubject == identity.Subject, ct).ConfigureAwait(false))
                {
                    return AlreadySignedUp();
                }

                throw;
            }

            return Results.Ok(StartSession(http, issuer, options, owner, tenant, time));
        })
        .WithName("AdminSignup")
        .WithSummary("Creates a tenant with this Google account as its owner, when the account is on the signup list.");

        group.MapPost("/session/end", async (HttpContext http, ScreenTailContext db, CancellationToken ct) =>
        {
            // Deleting the cookie ends this browser's copy. A copy taken from it before now (a stolen
            // cookie, a second browser) is ended on the server: the owner's session version moves on, and
            // every session that carries the old one is refused from the next request. Only for the
            // session's own page, which knows its CSRF value, so another site cannot sign the owner out.
            if (await AdminCaller.BindAsync(http).ConfigureAwait(false) is { } owner
                && CsrfHandler.Matches(owner.Csrf, http.Request.Headers[AdminSession.CsrfHeader].ToString()))
            {
                _ = await db.Owners
                    .Where(o => o.Id == owner.OwnerId && o.TenantId == owner.TenantId)
                    .ExecuteUpdateAsync(set => set.SetProperty(o => o.SessionVersion, o => o.SessionVersion + 1), ct)
                    .ConfigureAwait(false);
            }

            http.Response.Cookies.Delete(AdminSession.CookieName, AdminSession.Cookie(expires: null));
            return Results.NoContent();
        })
        .WithName("AdminSignOut")
        .WithSummary("Ends the owner's admin sessions: this browser's cookie, and on the server every session issued before now.");

        return group;
    }

    private static readonly IResult Refusal = Results.Json(
        new { error = "sign_in_failed", message = "Google did not confirm this sign-in. Try signing in again." },
        statusCode: StatusCodes.Status401Unauthorized);

    private static IResult NotAllowed() => Results.Json(
        new { error = "signup_not_allowed", message = "This Google account is not on the list allowed to set up ScreenTail. Ask your ScreenTail contact to add it, or sign in with the account that set up your organisation." },
        statusCode: StatusCodes.Status403Forbidden);

    private static IResult AlreadySignedUp() => Results.Json(
        new { error = "already_signed_up", message = "This Google account already runs an organisation in ScreenTail. Sign in instead." },
        statusCode: StatusCodes.Status409Conflict);

    /// <summary>Like a deployment with no vault key: true, and it names the setting.</summary>
    private static readonly IResult NotConfigured = Results.Json(
        new { error = "not_configured", message = $"Sign-in with Google is not set up on this deployment. The operator sets {AdminOptions.Section}:GoogleClientId to switch it on." },
        statusCode: StatusCodes.Status501NotImplemented);

    private static readonly IResult Unavailable = Results.Json(
        new { error = "sign_in_unavailable", message = "ScreenTail could not reach Google to check this sign-in. Try again in a minute." },
        statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>The identity, or the refusal to give: not configured, Google's keys out of reach, or a token that is not good.</summary>
    private static async Task<(GoogleIdentity? Identity, IResult Refusal)> VerifyAsync(string? credential, GoogleIdTokenVerifier google, AdminOptions options, CancellationToken ct)
    {
        if (!options.SignInConfigured)
        {
            return (null, NotConfigured);
        }

        try
        {
            return (await google.VerifyAsync(credential, ct).ConfigureAwait(false), Refusal);
        }
        catch (GoogleKeysUnavailableException)
        {
            return (null, Unavailable);
        }
    }

    private static AdminSessionResponse StartSession(HttpContext http, TokenIssuer issuer, AdminOptions options, Owner owner, Tenant tenant, TimeProvider time)
    {
        var csrf = AdminSession.NewCsrf();
        var (token, expires) = issuer.ForOwner(tenant.Id, owner.Id, owner.SessionVersion, csrf, options.SessionLifetime, time);
        http.Response.Cookies.Append(AdminSession.CookieName, token, AdminSession.Cookie(expires));
        return new AdminSessionResponse(AdminSessionResponse.SignedIn, csrf, expires, new AdminOwnerInfo(owner.Email, owner.DisplayName), new AdminTenantInfo(tenant.Id, tenant.Name, tenant.Seats));
    }
}
