namespace ScreenTail.Api.Endpoints;

/// <summary>
/// When a request arrived, before its body was read (2026-10-12 review, S-68).
///
/// The request's own timeout and the client's both count from about here, and a publish body can be
/// tens of megabytes of screenshots: over a slow uplink it takes a good part of either just to arrive.
/// A deadline counted from when the handler began would end after them, and the answer that carries
/// what landed would be an empty 504 instead. Recorded ahead of the request timeout so it is never
/// later than the moment that timeout starts counting.
/// </summary>
public static class RequestStart
{
    public static IApplicationBuilder UseRequestStart(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var time = app.ApplicationServices.GetRequiredService<TimeProvider>();
        return app.Use((context, next) =>
        {
            context.Features.Set(new Arrived(time.GetTimestamp()));
            return next(context);
        });
    }

    /// <summary>How long ago the request arrived, or zero when nothing recorded it.</summary>
    public static TimeSpan Elapsed(HttpContext context, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(time);
        return context.Features.Get<Arrived>() is { } arrived ? time.GetElapsedTime(arrived.Timestamp) : TimeSpan.Zero;
    }

    private sealed record Arrived(long Timestamp);
}
