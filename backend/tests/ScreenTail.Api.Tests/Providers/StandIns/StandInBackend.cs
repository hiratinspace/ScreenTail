using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Logging;
using ScreenTail.Api.Providers.ConnectWise;
using ScreenTail.Api.Providers.Hudu;
using ScreenTail.Api.Publish;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Providers.StandIns;

/// <summary>
/// The backend as it runs, pointed at the stand-ins: the real factories build the real providers from
/// the vault, with the real named clients and the real <c>PublicNetwork</c> handler, and the only
/// changes are settings a developer would make. <c>ProviderNetwork:AllowPrivateAddresses</c> is on, as
/// it may be only in Development, so the loopback stand-ins can be reached; the backoff is zero so a
/// retry does not wait for nothing; the provider clients trust the stand-ins' certificate by thumbprint.
///
/// The log is the service's own scrubbing sink at Trace, the most it can ever write, captured so a test
/// can show that nothing a provider was sent reached it (INV-10).
/// </summary>
internal sealed class StandInBackend : IAsyncDisposable
{
    private readonly ApiFixture _api;
    private readonly StringWriter _log = new();
    private readonly List<string> _content = [];
    private HttpClient? _client;

    public StandInBackend(ApiFixture api, ConnectWiseStandIn? cw, HuduStandIn? hudu, Action<IServiceCollection>? services = null)
    {
        _api = api;
        ConnectWise = cw;
        Hudu = hudu;
        Host = api.WithWebHostBuilder(builder =>
        {
            _ = builder.UseSetting("ConnectWise:ClientId", cw?.ClientId ?? string.Empty);
            _ = builder.UseSetting("ConnectWise:BackoffBase", "00:00:00");
            _ = builder.UseSetting("Hudu:BackoffBase", "00:00:00");
            _ = builder.UseSetting("ProviderNetwork:AllowPrivateAddresses", "true");
            _ = builder.UseSetting("Logging:LogLevel:Default", "Trace");
            _ = builder.ConfigureTestServices(collection =>
            {
                _ = collection.RemoveAll<ILoggerProvider>();
                _ = collection.AddSingleton<ILoggerProvider>(new ScrubbingLoggerProvider(_log, LogLevel.Trace));
                foreach (var name in new[] { nameof(ConnectWiseProvider), nameof(HuduProvider) })
                {
                    _ = collection.Configure<HttpClientFactoryOptions>(name, options =>
                        options.HttpMessageHandlerBuilderActions.Add(handler => StandInHost.TrustThisCertificateOnly(handler.PrimaryHandler)));
                }

                services?.Invoke(collection);
            });
        });
    }

    public WebApplicationFactory<Program> Host { get; }

    public ConnectWiseStandIn? ConnectWise { get; }

    public HuduStandIn? Hudu { get; }

    public Guid TenantId { get; private set; }

    /// <summary>The technician's authenticated client to the backend. Signed in on first use.</summary>
    public HttpClient Client => _client ?? throw new InvalidOperationException("Call SignInAsync first.");

    /// <summary>Everything the service logged so far.</summary>
    public string Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    /// <summary>
    /// A tenant with a device, signed in, with whichever stand-ins this backend has stored through the
    /// integrations endpoint the Settings card uses. A credential may be given to store a wrong one.
    /// </summary>
    public async Task SignInAsync(string? connectWiseCredential = null, string? huduKey = null, string displayName = "A Technician")
    {
        TenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        _ = await _api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = TenantId, Name = "Acme IT", Seats = 5, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = TenantId, Email = $"{userId:N}@acme.example", DisplayName = displayName, CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device { Id = deviceId, TenantId = TenantId, UserId = userId, Name = "TECH-LAPTOP", TokenHash = DeviceTokens.Hash(DeviceTokens.Create()), ActivatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });

        _client = Host.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _api.Issuer.ForDevice(TenantId, userId, deviceId).Token);
        if (ConnectWise is not null)
        {
            await StoreAsync("connectwise", ConnectWise.Site, connectWiseCredential ?? ConnectWise.Credential);
            Remember(ConnectWise.Credential, ConnectWise.PublicKey, ConnectWise.PrivateKey, ConnectWise.ClientId, connectWiseCredential);
            Remember([.. ConnectWise.Tickets.SelectMany(t => new[] { t.Summary, t.Company, t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) })]);
        }

        if (Hudu is not null)
        {
            await StoreAsync("hudu", Hudu.Site, huduKey ?? Hudu.ApiKey);
            Remember(Hudu.ApiKey, huduKey);
            Remember([.. Hudu.Companies.Select(c => c.Name).Where(n => !n.StartsWith("Company ", StringComparison.Ordinal))]);
        }
    }

    public async Task<PublishResponse> PublishAsync(PublishBundle bundle)
    {
        Remember(bundle.Note.Problem, bundle.Note.Result, bundle.Note.SuggestedTitle, bundle.Company, bundle.Reviewer);
        Remember([.. bundle.Note.Steps.Select(s => s.Text)]);
        using var response = await Client.PostAsJsonAsync(new Uri("/v1/sessions/publish", UriKind.Relative), bundle, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PublishResponse>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Adds strings to the list nothing in the log may contain: what a test itself sends that the bundle does not carry.</summary>
    public void Remember(params string?[] content) => _content.AddRange(content.Where(c => !string.IsNullOrWhiteSpace(c))!);

    /// <summary>
    /// INV-10, over everything this backend sent its providers and everything they answered: no note
    /// text, no ticket summary or number, no company, no key, no Basic credential, no site. The words
    /// are taken from the requests the stand-ins recorded as well as from what the test put in, so a
    /// field the provider sends that the test did not think of is covered too. The log is checked to
    /// carry the provider clients' own lines first, so an empty log cannot pass for a clean one.
    /// </summary>
    public void AssertTheLogHoldsNothingThatWasSent()
    {
        var log = Log;
        var sent = new List<string>(_content);
        var called = new StandInHost?[] { ConnectWise, Hudu }.OfType<StandInHost>().Where(s => s.Requests.Count > 0).ToList();
        Assert.NotEmpty(called);
        foreach (var stand in called)
        {
            Assert.Contains($"System.Net.Http.HttpClient.{(stand is ConnectWiseStandIn ? nameof(ConnectWiseProvider) : nameof(HuduProvider))}", log, StringComparison.Ordinal);
            sent.Add(stand.Site.Authority);
            foreach (var request in stand.Requests)
            {
                // The Basic credential both whole and as its base64, which is what a leak would quote.
                sent.AddRange(new[] { request.Header("Authorization"), request.Header("Authorization")?.Replace("Basic ", string.Empty, StringComparison.Ordinal), request.Header("clientId"), request.Header("x-api-key") }.OfType<string>());
                sent.AddRange(request.Query.Values);
                sent.AddRange(request.Form.Values);
                sent.AddRange(request.Files.Select(f => f.FileName));
                if (request.Body is { } body && body.TrimStart().StartsWith('{'))
                {
                    sent.AddRange(Strings(JsonDocument.Parse(body).RootElement));
                }
            }
        }

        // Fragments: a note is many lines, an article is HTML, and a log line that quoted one line of
        // either would be the leak. Short words (a flag's value, "Ticket") are protocol, not content.
        var fragments = sent
            .SelectMany(s => s.Split(['\n', '<', '>'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(s => s.Length >= 6 && s is not ("ServiceTicket" or "Billable" or "DoNotBill" or "Article" or "Problem" or "Result" or "Steps"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(fragments);
        var leaked = fragments.Where(f => log.Contains(f, StringComparison.Ordinal)).ToList();
        Assert.True(leaked.Count == 0, $"The log carries what was sent to a provider: {string.Join(" | ", leaked.Select(l => l.Length > 40 ? l[..40] : l))}");
    }

    public static PublishBundle Bundle(string ticketId = "731946", string? company = "Acme Dental") => new()
    {
        SessionId = "s-standin-0001",
        TicketId = ticketId,
        Company = company,
        NoteType = "internal",
        Minutes = 30,
        StartedAt = new DateTimeOffset(2026, 9, 24, 14, 2, 0, TimeSpan.Zero),
        Billable = true,
        Reviewer = "Riley Okafor",
        Footer = true,
        Destinations = ["ticket_note", "time_entry"],
        Note = new DraftJson
        {
            Problem = "Reception printer showed offline after the overnight update.",
            Steps =
            [
                new DraftStepJson { Text = "Restarted the print spooler on the front desk machine.", Confidence = "high", FrameRefs = ["f-1"] },
                new DraftStepJson { Text = "Cleared the stuck job queued from the scanner account.", Confidence = "medium", FrameRefs = ["f-2"] },
            ],
            Result = "Test page printed and the queue drains on its own again.",
            FollowUps = [],
            SuggestedTitle = "Reception printer offline after update",
            SuggestedTimeMinutes = 23,
            KbCandidate = true,
            KbReason = "Recurring after updates",
            Source = "cloud",
            PromptVersion = "note_v1",
        },
        Frames = [],
    };

    /// <summary>A frame as the client sends it: a JPEG's first bytes, base64. The stand-ins count bytes, so these are enough.</summary>
    public static PublishFrame Frame(string id) => new(id, "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/", "image/jpeg");

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        await Host.DisposeAsync();
        _log.Dispose();
    }

    private async Task StoreAsync(string provider, Uri site, string secret)
    {
        using var put = await Client.PutAsJsonAsync(new Uri($"/v1/integrations/{provider}", UriKind.Relative), new StoreIntegrationRequest(site.AbsoluteUri, secret), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
    }

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Strings(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        _ => [],
    };
}
