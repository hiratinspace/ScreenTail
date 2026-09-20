using System.Net;
using ScreenTail.Core.Net;

namespace ScreenTail.Tests.Net;

/// <summary>
/// ST-046 and INV-8: local-only mode means zero egress except a publish the technician asked for, and it
/// is enforced by an allowlist rather than by convention.
///
/// The distinction is the whole ticket, so the tests are written against an <c>HttpClient</c> built the
/// way the service builds one — a caller that wants to send something has to get past the handler, and
/// the assertions are about whether bytes reached the far side, not about what a policy object returned.
/// </summary>
public sealed class EgressGuardTests
{
    private static readonly Uri Backend = new("https://api.screentail.example/v1/summarise");
    private static readonly Uri Psa = new("https://acme.connectwise.example/v4/tickets/42/notes");
    private static readonly Uri Models = new("https://models.example/ggml-small.bin");

    private static EgressSettings Tenant(bool localOnly = false, bool policyEnforced = false) => new()
    {
        LocalOnly = localOnly,
        PolicyEnforced = policyEnforced,
        BackendHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api.screentail.example" },
        PublishHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "acme.connectwise.example", "acme.hudu.example" },
        ModelHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "models.example" },
    };

    [Fact]
    public async Task LocalOnlySendsNothingForDrafting()
    {
        // ST-046's first criterion, at the only place it can honestly be checked: whether the request
        // reached the network, not whether something decided it should not.
        var far = new CountingHandler();
        using var client = Client(Tenant(localOnly: true), far);

        var blocked = await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.SendAsync(EgressRequest.For(HttpMethod.Post, Backend, EgressPurpose.Summarisation), TestContext.Current.CancellationToken));

        Assert.Equal(0, far.Requests);
        Assert.Contains("Local-only mode is on", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalOnlyStopsEverythingElseTheClientWouldSayToTheBackend()
    {
        // Not only drafting. Telemetry, policy sync and licence checks are all the client talking about a
        // customer's machine without the technician asking it to.
        var far = new CountingHandler();
        using var client = Client(Tenant(localOnly: true), far);

        await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.SendAsync(EgressRequest.For(HttpMethod.Get, Backend, EgressPurpose.Backend), TestContext.Current.CancellationToken));

        Assert.Equal(0, far.Requests);
    }

    [Fact]
    public async Task LocalOnlyStillLetsTheTechnicianPublish()
    {
        // INV-8 draws the line at "user-initiated", not at "no network". A local-only mode that stopped
        // Publish would stop the product doing the one thing it exists for.
        var far = new CountingHandler();
        using var client = Client(Tenant(localOnly: true), far);

        var response = await client.SendAsync(
            EgressRequest.For(HttpMethod.Post, Psa, EgressPurpose.Publish), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, far.Requests);
    }

    [Fact]
    public async Task PublishOnlyReachesTheTenantsOwnHosts()
    {
        // ST-046's second criterion. Local-only being off is not permission to send a customer's
        // screenshots anywhere — the allowlist is a second, independent gate.
        var far = new CountingHandler();
        using var client = Client(Tenant(), far);

        var blocked = await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.SendAsync(
                EgressRequest.For(HttpMethod.Post, new Uri("https://pastebin.example/upload"), EgressPurpose.Publish),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, far.Requests);
        Assert.Equal("pastebin.example", blocked.Host);
    }

    [Fact]
    public async Task ARequestThatDoesNotSayWhatItIsForIsNotSent()
    {
        // A new caller that forgot. Defaulting to "probably fine" is how an egress guard ends up with a
        // hole nobody chose — so the default is refusal, and adding a caller is a decision somebody makes.
        var far = new CountingHandler();
        using var client = Client(Tenant(), far);

        await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Backend), TestContext.Current.CancellationToken));

        Assert.Equal(0, far.Requests);
    }

    [Fact]
    public async Task PlainHttpIsRefusedEvenToAnAllowedHost()
    {
        // Everything this client sends is either customer data or something that authenticates to it.
        var far = new CountingHandler();
        using var client = Client(Tenant(), far);

        await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.SendAsync(
                EgressRequest.For(HttpMethod.Post, new Uri("http://acme.connectwise.example/v4/tickets"), EgressPurpose.Publish),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, far.Requests);
    }

    [Fact]
    public async Task ASubdomainOfAnAllowedHostIsNotAnAllowedHost()
    {
        // Hosts are matched exactly. A rule that accepted anything ending in the tenant's domain is a rule
        // that trusts whoever can register a name under it.
        var far = new CountingHandler();
        using var client = Client(Tenant(), far);

        await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.SendAsync(
                EgressRequest.For(HttpMethod.Post, new Uri("https://evil.acme.connectwise.example/v4"), EgressPurpose.Publish),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, far.Requests);
    }

    [Fact]
    public async Task TheModelDownloadGoesToTheModelHostAndNowhereElse()
    {
        var far = new CountingHandler();
        using var client = Client(Tenant(), far);

        await client.SendAsync(EgressRequest.For(HttpMethod.Get, Models, EgressPurpose.ModelDownload), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.SendAsync(EgressRequest.For(HttpMethod.Get, Backend, EgressPurpose.ModelDownload), TestContext.Current.CancellationToken));

        Assert.Equal(1, far.Requests);
    }

    [Fact]
    public async Task ATenantThatConfiguredNothingSendsNothing()
    {
        // The default is empty allowlists, so a misconfigured or half-provisioned client cannot reach
        // anywhere at all. Failing closed is the only safe direction here.
        var far = new CountingHandler();
        using var client = Client(new EgressSettings(), far);

        foreach (var purpose in Enum.GetValues<EgressPurpose>())
        {
            await Assert.ThrowsAsync<EgressBlockedException>(
                () => client.SendAsync(EgressRequest.For(HttpMethod.Get, Psa, purpose), TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, far.Requests);
    }

    [Fact]
    public async Task BlocksAreCountedRatherThanDroppedSilently()
    {
        var far = new CountingHandler();
        var guard = new EgressGuard(new EgressPolicy(Tenant(localOnly: true)), far);
        using var client = new HttpClient(guard);

        await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.SendAsync(EgressRequest.For(HttpMethod.Post, Backend, EgressPurpose.Summarisation), TestContext.Current.CancellationToken));
        await client.SendAsync(EgressRequest.For(HttpMethod.Post, Psa, EgressPurpose.Publish), TestContext.Current.CancellationToken);

        Assert.Equal(1, guard.Blocked);
        Assert.Equal(1, guard.Allowed);
    }

    [Fact]
    public void ABlockedRequestSaysTheHostAndNothingElse()
    {
        // The message reaches a log and the diagnostics panel. A path or a query would carry a ticket
        // number, a subject, a customer's name (INV-10).
        var blocked = new EgressBlockedException(EgressPurpose.Publish, "pastebin.example", "not on the allowed list");

        Assert.Contains("pastebin.example", blocked.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdministratorsLocalOnlyCannotBeTurnedOffHere()
    {
        // INV-11. A setting that says "local only" while the UI lets it be switched off is a label, not a
        // guarantee, and the tooltip has to say who set it or the technician thinks it is their choice.
        var enforced = new EgressPolicy(Tenant(localOnly: true, policyEnforced: true));
        var chosen = new EgressPolicy(Tenant(localOnly: true));

        Assert.False(enforced.CanChangeLocally);
        Assert.Equal("Local-only (set by your administrator)", enforced.Badge);
        Assert.True(chosen.CanChangeLocally);
        Assert.Equal("Local-only", chosen.Badge);
    }

    [Fact]
    public void NoBadgeWhenLocalOnlyIsOff()
    {
        Assert.Null(new EgressPolicy(Tenant()).Badge);
    }

    [Fact]
    public async Task ARedirectToSomewhereElseIsRefusedLikeAnyOtherDestination()
    {
        // 2026-09-19 review. The guard decided on the URL it was handed, and the handler beneath it
        // followed redirects on its own — so the allowlist covered the first hop and nothing after it.
        // An allowed host answering 302 could send a session's bundle anywhere, and the counters would
        // record it as allowed.
        var far = new RedirectingHandler(new Uri("https://somewhere.else.example/collect"));
        using var client = Client(Tenant(), far);

        var blocked = await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.SendAsync(EgressRequest.For(HttpMethod.Get, Models, EgressPurpose.ModelDownload), TestContext.Current.CancellationToken));

        Assert.Equal("somewhere.else.example", blocked.Host);
        Assert.Equal(1, far.Requests);
    }

    [Fact]
    public async Task ARedirectWithinTheAllowlistIsFollowed()
    {
        // The model download needs this: the published URL redirects to a content host. Refusing every
        // redirect would be simpler and would mean narration never works.
        var far = new RedirectingHandler(new Uri("https://models.example/cdn/ggml-small.bin"));
        using var client = Client(Tenant(), far);

        using var response = await client.SendAsync(
            EgressRequest.For(HttpMethod.Get, Models, EgressPurpose.ModelDownload),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, far.Requests);
    }

    [Fact]
    public async Task ARedirectLoopStopsRatherThanSpinning()
    {
        var far = new RedirectingHandler(Models) { Always = true };
        using var client = Client(Tenant(), far);

        var blocked = await Assert.ThrowsAsync<EgressBlockedException>(
            () => client.SendAsync(EgressRequest.For(HttpMethod.Get, Models, EgressPurpose.ModelDownload), TestContext.Current.CancellationToken));

        Assert.Contains("redirect", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARedirectKeepsThePurposeItStartedWith()
    {
        // Otherwise the second hop arrives with no purpose and is refused for the wrong reason, which
        // reads as a bug in the guard rather than as a policy decision.
        var far = new RedirectingHandler(new Uri("https://acme.hudu.example/real"));
        using var client = Client(Tenant(localOnly: true), far);

        using var response = await client.SendAsync(
            EgressRequest.For(HttpMethod.Get, Psa, EgressPurpose.Publish),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task APostThatIsRedirectedIsNotQuietlyTurnedIntoAGet()
    {
        // 2026-09-20 review. Every redirect was followed as a bodiless GET, which is right for a 302 and
        // wrong for a 307 or a 308: those two exist precisely to say "same method, same body, new
        // address". A published note that met one became a GET to the new URL, and a 200 from whatever
        // answers a GET there reads as "sent" — the note is gone and the ticket never got it.
        //
        // Re-posting the body to the new host is the other wrong answer and is the thing this class
        // exists to prevent. So it is handed back: the caller sees the redirect and decides.
        var far = new RedirectingHandler(new Uri("https://acme.connectwise.example/v4/moved"))
        {
            Status = HttpStatusCode.PermanentRedirect,
        };
        using var client = Client(Tenant(localOnly: true), far);

        using var response = await client.SendAsync(
            EgressRequest.For(HttpMethod.Post, Psa, EgressPurpose.Publish),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
        Assert.Equal(1, far.Requests);
    }

    [Fact]
    public async Task AGetThatIsRedirectedWithA307IsStillFollowed()
    {
        // The method is what decides, not the status. A GET carries no body, so the hop changes nothing
        // and the model download still works.
        var far = new RedirectingHandler(new Uri("https://models.example/cdn/ggml-small.bin"))
        {
            Status = HttpStatusCode.TemporaryRedirect,
        };
        using var client = Client(Tenant(localOnly: true), far);

        using var response = await client.SendAsync(
            EgressRequest.For(HttpMethod.Get, Models, EgressPurpose.ModelDownload),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, far.Requests);
    }

    private static HttpClient Client(EgressSettings settings, HttpMessageHandler far) =>
        new(new EgressGuard(new EgressPolicy(settings), far));

    /// <summary>Answers the first request with a redirect, and the next with a 200.</summary>
    private sealed class RedirectingHandler(Uri to) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        /// <summary>Redirect every time, for the loop case.</summary>
        public bool Always { get; init; }

        /// <summary>Which kind of redirect. 307 and 308 keep the method; the others do not.</summary>
        public HttpStatusCode Status { get; init; } = HttpStatusCode.Found;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (Requests > 1 && !Always)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            var response = new HttpResponseMessage(Status);
            response.Headers.Location = to;
            return Task.FromResult(response);
        }
    }

    /// <summary>Stands in for the network. Counting requests is how "zero egress" is actually checked.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
