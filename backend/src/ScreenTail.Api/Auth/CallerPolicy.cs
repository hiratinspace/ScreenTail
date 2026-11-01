using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScreenTail.Api.Data;

namespace ScreenTail.Api.Auth;

/// <summary>
/// <see cref="CallerCheck"/> as an authorization policy, applied once to the whole /v1 group (ST-010;
/// 2026-10-12 review, T-173).
///
/// It used to be a call each handler had to remember. Twelve did, and only two had a test, so a handler
/// that lost the line would have served a revoked laptop for the rest of its token's hour. As a policy on
/// the group it holds for every route mapped there, written today or later, without anyone remembering.
///
/// <b>It runs in the authorization middleware, before the endpoint binds anything.</b> A caller who may
/// not act is refused before its body is read: it cannot spend 32 MB of our memory on a bundle, and it
/// cannot learn the bundle's rules from the binder's 400s.
/// </summary>
public static class CallerPolicy
{
    /// <summary>The policy's name, for <c>RequireAuthorization</c>.</summary>
    public const string Name = "ActiveCaller";

    /// <summary>Where the checked caller waits for the handler, so the database is asked once per request.</summary>
    internal static readonly object ItemKey = new();

    /// <summary>Authorization with the policy, its handler, and the refusal that says nothing about why.</summary>
    public static IServiceCollection AddCallerPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = services.AddAuthorization(options => options.AddPolicy(Name, policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(new ActiveCallerRequirement())));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, ActiveCallerHandler>());
        _ = services.Replace(ServiceDescriptor.Singleton<IAuthorizationMiddlewareResultHandler, CallerRefusal>());
        return services;
    }

    /// <summary>The caller the policy proved, or null when this request was not behind it.</summary>
    internal static Caller? Checked(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var stashed) ? stashed as Caller : null;
}

/// <summary>The device, the technician and the tenant behind a token are all still allowed to act.</summary>
public sealed class ActiveCallerRequirement : IAuthorizationRequirement;

/// <summary>Meets <see cref="ActiveCallerRequirement"/> by asking <see cref="CallerCheck"/>, and nothing else.</summary>
internal sealed class ActiveCallerHandler : AuthorizationHandler<ActiveCallerRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveCallerRequirement requirement)
    {
        // Endpoint routing hands the request over as the resource. Anything else is not a request this
        // policy knows how to judge, and it is left unmet: refused, not waved through.
        if (context.Resource is not HttpContext http || context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var db = http.RequestServices.GetRequiredService<ScreenTailContext>();
        if (await CallerCheck.ReadAsync(context.User, db, http.RequestAborted).ConfigureAwait(false) is { } caller)
        {
            http.Items[CallerPolicy.ItemKey] = caller;
            context.Succeed(requirement);
        }
    }
}

/// <summary>
/// Turns this policy's refusal into the same 401 a forged or expired token gets.
///
/// The framework answers an authenticated caller who fails a policy with 403, which would tell whoever
/// holds a stolen token that it was once good and that what changed is on our side. The same challenge,
/// the same empty body, and no hint of which check failed (INV-10 in spirit). Every other policy keeps
/// the framework's behaviour.
/// </summary>
internal sealed class CallerRefusal : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);

        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure?.FailedRequirements.Any(r => r is ActiveCallerRequirement or ActiveOwnerRequirement) == true)
        {
            if (policy.AuthenticationSchemes.Count == 0)
            {
                await context.ChallengeAsync().ConfigureAwait(false);
                return;
            }

            foreach (var scheme in policy.AuthenticationSchemes)
            {
                await context.ChallengeAsync(scheme).ConfigureAwait(false);
            }

            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
    }
}
