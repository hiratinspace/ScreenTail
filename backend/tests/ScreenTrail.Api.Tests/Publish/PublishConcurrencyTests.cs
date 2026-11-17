using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScreenTrail.Api.Auth;
using ScreenTrail.Api.Data;
using ScreenTrail.Api.Providers;
using ScreenTrail.Api.Providers.ConnectWise;
using ScreenTrail.Api.Publish;
using ScreenTrail.Api.Summarize;

namespace ScreenTrail.Api.Tests.Publish;

/// <summary>
/// How many publishes one device and one tenant may have in flight at once (2026-11-15 review). A
/// publish holds up to 32 MB until its PSA answers, and the PSA is the tenant's choice, so the gate is
/// in front of the body: a request over the limit is refused as busy before a byte of it is read, in
/// the shape the client shows the technician, and the places come back when the publishes end.
/// </summary>
public sealed class PublishConcurrencyTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly Uri Route = new("/v1/sessions/publish", UriKind.Relative);

    [Fact]
    public void ADeviceMayHaveTwoPublishesInFlightAndNotAThird()
    {
        var gate = new PublishConcurrency();
        var tenant = Guid.NewGuid();
        var device = Guid.NewGuid();

        using var first = gate.TryEnter(tenant, device, out _);
        using var second = gate.TryEnter(tenant, device, out _);
        var third = gate.TryEnter(tenant, device, out var refusal);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(third);
        Assert.Contains("This device", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ATenantMayHaveTenPublishesInFlightAndARefusalKeepsNoDevicesPlace()
    {
        var gate = new PublishConcurrency();
        var tenant = Guid.NewGuid();
        var leases = Enumerable.Range(0, PublishConcurrency.PerTenant).Select(i => gate.TryEnter(tenant, Guid.NewGuid(), out _)).ToList();
        var device = Guid.NewGuid();

        var over = gate.TryEnter(tenant, device, out var refusal);
        using var elsewhere = gate.TryEnter(Guid.NewGuid(), Guid.NewGuid(), out _);

        Assert.All(leases, Assert.NotNull);
        Assert.Null(over);
        Assert.Contains("Your team", refusal, StringComparison.Ordinal);
        Assert.NotNull(elsewhere);

        // Turned away by the tenant, the device was not counted: once the tenant has room it has both.
        leases.ForEach(lease => lease!.Dispose());
        using var first = gate.TryEnter(tenant, device, out _);
        using var second = gate.TryEnter(tenant, device, out _);
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public void APlaceIsGivenBackOnceAndNothingIsKeptAfterwards()
    {
        var gate = new PublishConcurrency();
        var tenant = Guid.NewGuid();
        var device = Guid.NewGuid();

        var lease = gate.TryEnter(tenant, device, out _)!;
        lease.Dispose();
        lease.Dispose();

        Assert.Equal(0, gate.Tracked);
        using var first = gate.TryEnter(tenant, device, out _);
        using var second = gate.TryEnter(tenant, device, out _);
        Assert.Null(gate.TryEnter(tenant, device, out _));
    }

    [Fact]
    public async Task ADeviceAlreadyPublishingAsMuchAsItMayIsToldItIsBusyBeforeItsBodyIsRead()
    {
        var psa = new HoldingPsa();
        using var host = Host(psa);
        var tenant = Guid.NewGuid();
        var device = await SeedAsync(tenant);
        using var client = Client(host, tenant, device);

        var held = Enumerable.Range(0, PublishConcurrency.PerDevice).Select(_ => client.PostAsJsonAsync(Route, Bundle(), TestContext.Current.CancellationToken)).ToList();
        await psa.WaitForAsync(PublishConcurrency.PerDevice);

        // Not JSON at all: refused as busy rather than as malformed, so nothing read the body first.
        using var refused = await client.PostAsync(Route, new StringContent("{ this is not a publish", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(PublishConcurrency.RetryAfterSeconds), refused.Headers.RetryAfter?.Delta);
        var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal("busy", body.GetProperty("error").GetString());
        Assert.Contains("publish again in a moment", body.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(PublishConcurrency.PerDevice, psa.Notes);

        psa.Release();
        foreach (var response in await Task.WhenAll(held))
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        // And the places are given back once those finish.
        using var after = await client.PostAsJsonAsync(Route, Bundle(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task ATenantAlreadyPublishingAsMuchAsItMayIsToldItIsBusyAndOthersAreNot()
    {
        var psa = new HoldingPsa();
        using var host = Host(psa);
        var tenant = Guid.NewGuid();
        var clients = new List<HttpClient>();
        for (var i = 0; i <= PublishConcurrency.PerTenant; i++)
        {
            clients.Add(Client(host, tenant, await SeedAsync(tenant, newTenant: i == 0)));
        }

        var held = clients.Take(PublishConcurrency.PerTenant).Select(client => client.PostAsJsonAsync(Route, Bundle(), TestContext.Current.CancellationToken)).ToList();
        await psa.WaitForAsync(PublishConcurrency.PerTenant);

        using var refused = await clients[^1].PostAsJsonAsync(Route, Bundle(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // Another tenant is not held up by this one.
        var other = Guid.NewGuid();
        using var otherClient = Client(host, other, await SeedAsync(other));
        var elsewhere = otherClient.PostAsJsonAsync(Route, Bundle(), TestContext.Current.CancellationToken);
        await psa.WaitForAsync(PublishConcurrency.PerTenant + 1);

        psa.Release();
        using (var response = await elsewhere)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        foreach (var response in await Task.WhenAll(held))
        {
            response.Dispose();
        }

        clients.ForEach(client => client.Dispose());
    }

    [Fact]
    public async Task OverKestrelARefusedPublishWithALargeSlowBodyStillGetsItsRefusal()
    {
        // 2026-11-15 review of the gate: Kestrel drains a body the application left unread for about
        // five seconds and then drops the connection, so a large publish on a slow uplink saw a reset,
        // which the client reads as "may have landed", instead of the busy answer. The real server, and a
        // body that takes longer than that to arrive after it is refused.
        var psa = new HoldingPsa();
        using var host = Host(psa);
        host.UseKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        host.StartServer();
        var address = new Uri(host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First());
        var tenant = Guid.NewGuid();
        var who = await SeedAsync(tenant);
        using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Issuer.ForDevice(tenant, who.User, who.Device).Token);

        var held = Enumerable.Range(0, PublishConcurrency.PerDevice).Select(_ => client.PostAsJsonAsync(Route, Bundle(), TestContext.Current.CancellationToken)).ToList();
        await psa.WaitForAsync(PublishConcurrency.PerDevice);

        // Six megabytes at about 640 KB a second: some ten seconds of upload. Without the body read into
        // nothing first, this failed after about seven with a broken pipe. Smaller bodies fit in the
        // loopback socket's buffers and do not show it.
        using var slow = new ThrottledContent(6 * 1024 * 1024, chunk: 64 * 1024, pause: TimeSpan.FromMilliseconds(100));
        using var refused = await client.PostAsync(Route, slow, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("busy", JsonDocument.Parse(await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("error").GetString());
        Assert.Equal(slow.Length, slow.Sent);

        psa.Release();
        foreach (var response in await Task.WhenAll(held))
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task APublishWhosePsaThrowsGivesItsPlaceBack()
    {
        var gate = new PublishConcurrency();
        var psa = new HoldingPsa { Throws = true };
        using var host = Host(psa, gate);
        var tenant = Guid.NewGuid();
        using var client = Client(host, tenant, await SeedAsync(tenant));

        var broken = Enumerable.Range(0, PublishConcurrency.PerDevice).Select(_ => client.PostAsJsonAsync(Route, Bundle(), TestContext.Current.CancellationToken)).ToList();
        await psa.WaitForAsync(PublishConcurrency.PerDevice);
        psa.Release();
        foreach (var response in await Task.WhenAll(broken))
        {
            response.Dispose();
        }

        await Until(() => gate.Tracked == 0);
        await BothPlacesAreFreeAsync(client, psa);
    }

    [Fact]
    public async Task APublishTheClientGaveUpOnGivesItsPlaceBack()
    {
        var gate = new PublishConcurrency();
        var psa = new HoldingPsa();
        using var host = Host(psa, gate);
        var tenant = Guid.NewGuid();
        using var client = Client(host, tenant, await SeedAsync(tenant));

        using (var giveUp = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            var abandoned = client.PostAsJsonAsync(Route, Bundle(), giveUp.Token);
            await psa.WaitForAsync(1);
            Assert.Equal(2, gate.Tracked);
            await giveUp.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        }

        await Until(() => gate.Tracked == 0);
        await BothPlacesAreFreeAsync(client, psa);
    }

    private static PublishBundle Bundle() => new()
    {
        SessionId = "s-0001",
        TicketId = "48213",
        Destinations = ["ticket_note"],
        Note = new DraftJson
        {
            Problem = "Printer offline in reception.",
            Steps = [new DraftStepJson { Text = "Restarted the spooler.", Confidence = "high" }],
            Result = "Printing works again.",
            SuggestedTitle = "Printer offline: spooler stopped",
            SuggestedTimeMinutes = 10,
            KbCandidate = false,
            KbReason = "One-off fix",
            Source = "cloud",
            PromptVersion = "note_v2",
        },
        Frames = [],
    };

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host(HoldingPsa psa, PublishConcurrency? gate = null) => api.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            _ = services.Replace(ServiceDescriptor.Scoped<IPsaProviderFactory>(_ => psa));
            if (gate is not null)
            {
                _ = services.AddSingleton(gate);
            }
        }));

    /// <summary>Waits until the condition holds, failing the test after half a minute rather than hanging the run.</summary>
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>
    /// Both of the device's places are free: two publishes from it are held by the PSA at once, then
    /// released, and both land.
    /// </summary>
    private static async Task BothPlacesAreFreeAsync(HttpClient client, HoldingPsa psa)
    {
        psa.Rearm();
        var before = psa.Notes;
        var two = Enumerable.Range(0, PublishConcurrency.PerDevice).Select(_ => client.PostAsJsonAsync(Route, Bundle(), TestContext.Current.CancellationToken)).ToList();
        await psa.WaitForAsync(before + PublishConcurrency.PerDevice);
        psa.Release();
        foreach (var response in await Task.WhenAll(two))
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var landed = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("results")[0];
                Assert.True(landed.GetProperty("ok").GetBoolean());
            }
        }
    }

    private HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, Guid tenant, (Guid User, Guid Device) who)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Issuer.ForDevice(tenant, who.User, who.Device).Token);
        return client;
    }

    private async Task<(Guid User, Guid Device)> SeedAsync(Guid tenantId, bool newTenant = true)
    {
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        _ = await api.UseAsync(async db =>
        {
            if (newTenant)
            {
                db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme IT", Seats = 50, CreatedAt = DateTimeOffset.UtcNow });
            }

            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId:N}@acme.example", DisplayName = "A Technician", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device { Id = deviceId, TenantId = tenantId, UserId = userId, Name = "TECH-LAPTOP", TokenHash = DeviceTokens.Hash(DeviceTokens.Create()), ActivatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return (userId, deviceId);
    }

    /// <summary>A body of <see cref="Length"/> bytes sent a chunk at a time with a pause between: a slow uplink.</summary>
    private sealed class ThrottledContent(int length, int chunk, TimeSpan pause) : HttpContent
    {
        private long _sent;

        public int Length => length;

        public long Sent => Interlocked.Read(ref _sent);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var buffer = new byte[chunk];
            Array.Fill(buffer, (byte)' ');
            for (var left = length; left > 0; left -= chunk)
            {
                var size = Math.Min(chunk, left);
                await stream.WriteAsync(buffer.AsMemory(0, size));
                await stream.FlushAsync();
                _ = Interlocked.Add(ref _sent, size);
                await Task.Delay(pause);
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }

    /// <summary>
    /// A PSA whose notes wait until <see cref="Release"/>: a slow site, as slow as the test makes it. With
    /// <see cref="Throws"/> set, a released note throws instead of landing.
    /// </summary>
    private sealed class HoldingPsa : IPsaProviderFactory, IPsaProvider
    {
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _notes;

        public string Name => "connectwise";

        public int Notes => Volatile.Read(ref _notes);

        public bool Throws { get; set; }

        public void Release() => Volatile.Read(ref _release).TrySetResult();

        /// <summary>Holds the notes that arrive from now on, until the next <see cref="Release"/>.</summary>
        public void Rearm()
        {
            Throws = false;
            Volatile.Write(ref _release, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public async Task WaitForAsync(int notes)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (Notes < notes)
            {
                await Task.Delay(10, timeout.Token);
            }
        }

        public Task<IPsaProvider?> ForTenantAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult<IPsaProvider?>(this);

        public Task<ProviderResult<bool>> CheckAsync(CancellationToken ct = default) => Task.FromResult(ProviderResult.Success(true));

        public Task<ProviderResult<IReadOnlyList<TicketRef>>> SearchTicketsAsync(string query, CancellationToken ct = default) => Task.FromResult(ProviderResult.Success<IReadOnlyList<TicketRef>>([]));

        public Task<ProviderResult<IReadOnlyList<TicketRef>>> RecentTicketsAsync(CancellationToken ct = default) => Task.FromResult(ProviderResult.Success<IReadOnlyList<TicketRef>>([]));

        public Task<ProviderResult<CompanyRef?>> FindCompanyAsync(string name, CancellationToken ct = default) => Task.FromResult(ProviderResult.Success<CompanyRef?>(null));

        public async Task<ProviderResult<PublishedNote>> AddNoteAsync(TicketNote note, CancellationToken ct = default)
        {
            var release = Volatile.Read(ref _release);
            _ = Interlocked.Increment(ref _notes);
            await release.Task.WaitAsync(ct);
            return Throws
                ? throw new InvalidOperationException("The PSA's client broke.")
                : ProviderResult.Success(new PublishedNote("90001", null));
        }

        public Task<ProviderResult<PublishedNote>> AddTimeEntryAsync(TimeEntry entry, CancellationToken ct = default) => Task.FromResult(ProviderResult.Success(new PublishedNote("70001", null)));
    }
}
