namespace ScreenTail.Api.Endpoints;

/// <summary>
/// The headers every answer of this API carries (ST-099 hardening, ADR-0009). The API serves JSON and
/// nothing a browser should render, run or frame, so its policy is the closed one: no source of any
/// kind, no framing, no base, no form target, no sniffing, no referrer. The web admin's page has its
/// own, looser policy that lets Google's sign-in in (<c>web/security-headers.ts</c>), sent by whatever
/// serves the page.
///
/// Added as the headers are sent rather than as the request comes in, so an answer that the framework
/// clears and rewrites on the way out (an unhandled error, a refusal from a later middleware) has them too.
/// </summary>
public static class SecurityHeaders
{
    public const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    public const string PermissionsPolicy = "accelerometer=(), camera=(), display-capture=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                // A route that set its own (the demo PSA's page allows its inline styles) keeps it: this is
                // the default for answers that said nothing, never a replacement.
                var headers = ((HttpResponse)state).Headers;
                _ = headers.TryAdd("Content-Security-Policy", ContentSecurityPolicy);
                _ = headers.TryAdd("X-Content-Type-Options", "nosniff");
                _ = headers.TryAdd("X-Frame-Options", "DENY");
                _ = headers.TryAdd("Referrer-Policy", "no-referrer");
                _ = headers.TryAdd("Permissions-Policy", PermissionsPolicy);
                _ = headers.TryAdd("Cross-Origin-Resource-Policy", "same-origin");
                return Task.CompletedTask;
            }, context.Response);
            return next(context);
        });
}
