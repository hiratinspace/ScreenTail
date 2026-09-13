namespace ScreenTail.Core.Net;

/// <summary>A request the egress policy refused. Carries no URL path and no payload (INV-10).</summary>
public sealed class EgressBlockedException(EgressPurpose purpose, string host, string reason)
    : Exception($"Blocked a {purpose} request to {host}: {reason}")
{
    public EgressPurpose Purpose { get; } = purpose;

    public string Host { get; } = host;
}

/// <summary>
/// The handler every <c>HttpClient</c> in the client is built with (ST-046, INV-8).
///
/// INV-8 asks for an allowlist rather than a convention, and this is where that distinction becomes real:
/// a caller cannot forget to consult the policy, because the policy sits in the pipeline underneath them.
/// Code that wants to send something has to go through here, and code that does not go through here has
/// no <c>HttpClient</c> to send it with.
///
/// <b>The purpose comes from the caller, not from the URL.</b> Guessing it from the host would mean the
/// guard learns what a request is for from the same thing it is meant to be checking. A request with no
/// purpose attached is refused rather than assumed to be harmless, which also makes adding a new caller a
/// decision somebody has to make rather than one that happens by default.
///
/// Blocks are counted rather than dropped silently: "local-only mode was on and nothing was sent" is a
/// claim a customer may ask this software to stand behind. Writing the host to the audit log is ST-045's
/// job and lands with it; the counters are here so the diagnostics panel has something to show meanwhile.
/// </summary>
public sealed class EgressGuard(EgressPolicy policy, HttpMessageHandler? inner = null)
    : DelegatingHandler(inner ?? new HttpClientHandler())
{
    /// <summary>Where a caller declares what a request is for. Without it the request does not go out.</summary>
    public static readonly HttpRequestOptionsKey<EgressPurpose> PurposeKey = new("ScreenTail.EgressPurpose");

    /// <summary>How many requests have been refused. Shown in the diagnostics panel; counts only.</summary>
    public long Blocked { get; private set; }

    public long Allowed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var destination = request.RequestUri
            ?? throw new EgressBlockedException(EgressPurpose.Backend, "unknown", "The request had no destination.");

        if (!request.Options.TryGetValue(PurposeKey, out var purpose))
        {
            Blocked++;
            throw new EgressBlockedException(
                EgressPurpose.Backend,
                destination.Host,
                "The request did not say what it was for, so it was not sent.");
        }

        var decision = policy.Decide(purpose, destination);
        if (!decision.Allowed)
        {
            Blocked++;
            throw new EgressBlockedException(purpose, destination.Host, decision.Reason);
        }

        Allowed++;
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Attaches a purpose to a request, so it is one call rather than four lines at every caller.</summary>
public static class EgressRequest
{
    public static HttpRequestMessage For(HttpMethod method, Uri destination, EgressPurpose purpose)
    {
        var request = new HttpRequestMessage(method, destination);
        request.Options.Set(EgressGuard.PurposeKey, purpose);
        return request;
    }

    public static HttpRequestMessage WithPurpose(this HttpRequestMessage request, EgressPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(EgressGuard.PurposeKey, purpose);
        return request;
    }
}
