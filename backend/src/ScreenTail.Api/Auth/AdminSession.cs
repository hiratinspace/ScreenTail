using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Auth;

/// <summary>
/// The web admin's session (ST-099, ADR-0009): a ScreenTail token for a tenant's owner, in an HttpOnly,
/// Secure, SameSite=Strict cookie, never in an Authorization header, with a CSRF value that every change
/// must repeat in <see cref="CsrfHeader"/>.
///
/// It is a second authentication scheme beside the devices' bearer one, and the two never overlap:
/// this one reads only the cookie and accepts only <see cref="Audience"/>; the device scheme reads only
/// the header and accepts only the devices' audience. Admin routes are behind <see cref="OwnerPolicy"/>,
/// which authenticates with this scheme alone; device routes are behind <see cref="CallerPolicy"/>,
/// which authenticates with the default one alone.
/// </summary>
public static class AdminSession
{
    public const string Scheme = "AdminSession";

    /// <summary><c>__Host-</c>: the browser keeps it only if it is Secure, has path / and names no domain.</summary>
    public const string CookieName = "__Host-screentail-admin";

    public const string CsrfHeader = "X-ScreenTail-CSRF";

    /// <summary>The admin session's audience. <see cref="AdminOptions.Validate"/> refuses a device audience equal to it.</summary>
    public const string Audience = "screentail-admin";

    public const string OwnerRole = "owner";

    /// <summary>The cookie's settings, the same for writing and for deleting it.</summary>
    public static CookieOptions Cookie(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expires,
        IsEssential = true,
    };

    /// <summary>A fresh CSRF value: 256 random bits, URL-safe.</summary>
    public static string NewCsrf() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The admin scheme: the cookie, the admin audience, the same key and the same pinned algorithm.</summary>
    public static AuthenticationBuilder AddAdminSession(this AuthenticationBuilder builder, JwtOptions jwt)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var validation = TokenIssuer.AdminValidation(jwt);
        return builder.AddJwtBearer(Scheme, options =>
        {
            options.TokenValidationParameters = validation;
            options.IncludeErrorDetails = false;
            options.MapInboundClaims = false;
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // The cookie and nothing else. Left unset, the bearer handler would read the
                    // Authorization header, which is where a device's token travels.
                    if (context.Request.Cookies.TryGetValue(CookieName, out var token) && !string.IsNullOrEmpty(token))
                    {
                        context.Token = token;
                    }
                    else
                    {
                        context.NoResult();
                    }

                    return Task.CompletedTask;
                },
            };
        });
    }
}

/// <param name="Csrf">The value the session carries, which a change must repeat in <see cref="AdminSession.CsrfHeader"/>.</param>
/// <param name="ExpiresAt">When the session ends and the owner signs in again.</param>
public sealed record AdminCaller(Guid TenantId, Guid OwnerId, Owner Owner, Tenant Tenant, string Csrf, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// How an admin handler asks for its caller, bound from what <see cref="OwnerPolicy"/> proved. A route
    /// mapped outside the policy is checked here and then; one that may not act binds to nothing.
    /// </summary>
    public static async ValueTask<AdminCaller?> BindAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (OwnerPolicy.Checked(context) is { } proved)
        {
            return proved;
        }

        var result = await context.AuthenticateAsync(AdminSession.Scheme).ConfigureAwait(false);
        return result.Succeeded
            ? await OwnerCheck.ReadAsync(result.Principal, context.RequestServices.GetRequiredService<ScreenTailContext>(), context.RequestAborted).ConfigureAwait(false)
            : null;
    }
}

/// <summary>Whether the holder of a good admin session may still act: the owner and the tenant both exist and are not disabled.</summary>
public static class OwnerCheck
{
    public static async Task<AdminCaller?> ReadAsync(ClaimsPrincipal principal, ScreenTailContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(db);

        // An owner's session says so, and says which owner. A token with a device id in it is a device's,
        // whatever else it carries.
        if (principal.FindFirstValue(ScreenTailClaims.Role) != AdminSession.OwnerRole
            || principal.FindFirst(ScreenTailClaims.DeviceId) is not null
            || !Guid.TryParse(principal.FindFirstValue(ScreenTailClaims.TenantId), out var tenantId)
            || !Guid.TryParse(principal.FindFirstValue(ScreenTailClaims.OwnerId), out var ownerId)
            || principal.FindFirstValue(ScreenTailClaims.Csrf) is not { Length: > 0 } csrf
            || !long.TryParse(principal.FindFirstValue("exp"), out var exp))
        {
            return null;
        }

        var owner = await db.Owners
            .AsNoTracking()
            .Include(o => o.Tenant)
            .SingleOrDefaultAsync(o => o.Id == ownerId && o.TenantId == tenantId, ct)
            .ConfigureAwait(false);

        return owner is { DisabledAt: null, Tenant: { DisabledAt: null } tenant }
            ? new AdminCaller(tenantId, ownerId, owner, tenant, csrf, DateTimeOffset.FromUnixTimeSeconds(exp))
            : null;
    }
}

/// <summary>
/// The owner's policy for every admin route: authenticated by <see cref="AdminSession.Scheme"/> alone, an
/// owner who may still act, and for anything but a read, the CSRF header matching the session.
/// </summary>
public static class OwnerPolicy
{
    public const string Name = "ActiveOwner";

    internal static readonly object ItemKey = new();

    public static IServiceCollection AddOwnerPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _ = services.AddAuthorization(options => options.AddPolicy(Name, policy => policy
            .AddAuthenticationSchemes(AdminSession.Scheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new ActiveOwnerRequirement(), new CsrfRequirement())));
        _ = services.AddSingleton<IAuthorizationHandler, ActiveOwnerHandler>();
        _ = services.AddSingleton<IAuthorizationHandler, CsrfHandler>();
        return services;
    }

    internal static AdminCaller? Checked(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var stashed) ? stashed as AdminCaller : null;
}

/// <summary>The owner and the tenant behind an admin session are still allowed to act.</summary>
public sealed class ActiveOwnerRequirement : IAuthorizationRequirement;

/// <summary>A change carries the session's CSRF value in <see cref="AdminSession.CsrfHeader"/>.</summary>
public sealed class CsrfRequirement : IAuthorizationRequirement;

internal sealed class ActiveOwnerHandler : AuthorizationHandler<ActiveOwnerRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveOwnerRequirement requirement)
    {
        if (context.Resource is not HttpContext http || context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var db = http.RequestServices.GetRequiredService<ScreenTailContext>();
        if (await OwnerCheck.ReadAsync(context.User, db, http.RequestAborted).ConfigureAwait(false) is { } owner)
        {
            http.Items[OwnerPolicy.ItemKey] = owner;
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// The double-submit check (ADR-0009). SameSite=Strict already keeps the cookie off another site's
/// requests; this is the second lock, for a sibling site that counts as the same site and for a browser
/// that ignores SameSite. The value lives in the signed session, so a page that cannot read our responses
/// cannot know it.
/// </summary>
internal sealed class CsrfHandler : AuthorizationHandler<CsrfRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CsrfRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            return Task.CompletedTask;
        }

        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var expected = context.User.FindFirstValue(ScreenTailClaims.Csrf);
        var presented = http.Request.Headers[AdminSession.CsrfHeader].ToString();
        if (!string.IsNullOrEmpty(expected)
            && !string.IsNullOrEmpty(presented)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
