using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Tests.Admin;

/// <summary>
/// The owner's pages on the server (ST-099): technicians and seats, invites, revoking a device, the
/// policy, integration status and the download link. Every test that reads also seeds a second tenant
/// and proves none of its rows comes back, and every test that writes proves the other tenant's rows
/// are unchanged.
/// </summary>
public sealed class AdminEndpointsTests(AdminApiFixture api) : IClassFixture<AdminApiFixture>
{
    [Fact]
    public async Task TheOwnerSignsUpInvitesATechSetsAPolicyAndSeesTheDeviceActivate()
    {
        // ST-099's acceptance criterion, end to end over HTTP, with the device's side done by the same
        // endpoint the Windows client calls.
        using var browser = api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var token = api.Google.Mint(subject: "sub-ac", email: AdminApiFixture.Allowed[10]);
        using var signedUp = await browser.PostAsJsonAsync(new Uri("/v1/admin/signup", UriKind.Relative), new AdminSignupRequest(token, "Acme IT"), TestContext.Current.CancellationToken);
        var session = await signedUp.Content.ReadFromJsonAsync<AdminSessionResponse>(TestContext.Current.CancellationToken);
        browser.DefaultRequestHeaders.Add("X-ScreenTail-CSRF", session!.Csrf);

        using var invited = await browser.PostAsJsonAsync(new Uri("/v1/admin/invites", UriKind.Relative), new CreateInviteRequest("T.Ortiz@Acme.example", "Tomas Ortiz"), TestContext.Current.CancellationToken);
        var invite = await invited.Content.ReadFromJsonAsync<CreatedInvite>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, invited.StatusCode);
        Assert.Matches("^[A-Z2-9]{5}(-[A-Z2-9]{5}){3}$", invite!.Code);
        Assert.Equal("t.ortiz@acme.example", invite.Email);

        using var policy = await browser.PutAsJsonAsync(new Uri("/v1/admin/policy", UriKind.Relative), Policy(retention: 3, localOnly: true, locked: true), TestContext.Current.CancellationToken);
        var set = await policy.Content.ReadFromJsonAsync<TenantPolicyResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);

        var before = await browser.GetFromJsonAsync<TechniciansResponse>(new Uri("/v1/admin/technicians", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(new SeatUsage(0, AdminApiFixture.NewTenantSeats), before!.Seats);
        Assert.Equal("pending", Assert.Single(before.Invites).Status);

        using var device = api.CreateClient();
        using var activated = await device.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(invite.Code, "TECH-LAPTOP"), TestContext.Current.CancellationToken);
        var bound = await activated.Content.ReadFromJsonAsync<ActivatedDevice>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal("Acme IT", bound!.TenantName);

        var after = await browser.GetFromJsonAsync<TechniciansResponse>(new Uri("/v1/admin/technicians", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(new SeatUsage(1, AdminApiFixture.NewTenantSeats), after!.Seats);
        Assert.Equal("accepted", Assert.Single(after.Invites).Status);
        var tech = Assert.Single(after.Technicians);
        Assert.Equal("Tomas Ortiz", tech.DisplayName);
        Assert.Equal("TECH-LAPTOP", Assert.Single(tech.Devices).Name);

        // And the device receives the policy the owner set.
        device.DefaultRequestHeaders.Authorization = new("Bearer", bound.AccessToken);
        var received = await device.GetFromJsonAsync<TenantPolicyResponse>(new Uri("/v1/policy", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(set, received);
        Assert.Equal(3, received!.RetentionDays);
        Assert.True(received.LocalOnlyLocked);
    }

    [Fact]
    public async Task TheTechnicianListHoldsOnlyThisTenantsPeopleDevicesAndInvites()
    {
        var mine = await api.SeedOwnerAsync(seats: 4);
        var theirs = await api.SeedOwnerAsync("Birch Legal");
        _ = await api.SeedDeviceAsync(mine.TenantId, "MINE-1");
        var (_, revokedId, _) = await api.SeedDeviceAsync(mine.TenantId, "MINE-2");
        _ = await api.UseAsync(db => db.Devices.Where(d => d.Id == revokedId).ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow)));
        _ = await api.SeedDeviceAsync(theirs.TenantId, "THEIRS-1");
        _ = await api.UseAsync(db => Invites.CreateAsync(db, mine.TenantId, "new@acme.example", "New Tech", TimeProvider.System));
        _ = await api.UseAsync(db => Invites.CreateAsync(db, theirs.TenantId, "other@birch.example", "Other Tech", TimeProvider.System));
        using var client = api.Owner(mine);

        var list = await client.GetFromJsonAsync<TechniciansResponse>(new Uri("/v1/admin/technicians", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(new SeatUsage(1, 4), list!.Seats);
        Assert.Equal(["MINE-1", "MINE-2"], list.Technicians.SelectMany(t => t.Devices).Select(d => d.Name).Order(StringComparer.Ordinal));
        Assert.Equal("new@acme.example", Assert.Single(list.Invites).Email);
        var body = await client.GetStringAsync(new Uri("/v1/admin/technicians", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("THEIRS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("birch", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvitesCodeIsShownOnceAndOnlyItsHashIsKept()
    {
        var owner = await api.SeedOwnerAsync();
        using var client = api.Owner(owner);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/admin/invites", UriKind.Relative), new CreateInviteRequest("tech@acme.example", null), TestContext.Current.CancellationToken);
        var invite = await response.Content.ReadFromJsonAsync<CreatedInvite>(TestContext.Current.CancellationToken);
        var stored = await api.UseAsync(db => db.Invites.SingleAsync(i => i.Id == invite!.Id));
        var listed = await client.GetStringAsync(new Uri("/v1/admin/technicians", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(owner.TenantId, stored.TenantId);
        Assert.Equal(Invites.Hash(invite!.Code), stored.CodeHash);
        Assert.Equal(Invites.Lifetime, stored.ExpiresAt - stored.CreatedAt);
        Assert.Equal("tech@acme.example", stored.DisplayName);
        Assert.DoesNotContain(invite.Code, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(Invites.Normalise(invite.Code), listed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("two@@acme.example")]
    [InlineData("spaces in@acme.example")]
    [InlineData("@acme.example")]
    [InlineData("tech@")]
    public async Task AnInviteNeedsAnAddress(string? email)
    {
        var owner = await api.SeedOwnerAsync();
        using var client = api.Owner(owner);

        using var response = await client.PostAsJsonAsync(new Uri("/v1/admin/invites", UriKind.Relative), new CreateInviteRequest(email, "Tech"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await api.UseAsync(db => db.Invites.CountAsync(i => i.TenantId == owner.TenantId)));
    }

    [Fact]
    public async Task RevokingADeviceStopsItsTokenAndFreesItsSeat()
    {
        var owner = await api.SeedOwnerAsync();
        var (_, deviceId, deviceToken) = await api.SeedDeviceAsync(owner.TenantId);
        using var client = api.Owner(owner);
        using var device = api.Device(deviceToken);
        using var working = await device.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, working.StatusCode);

        using var revoked = await client.PostAsync(new Uri($"/v1/admin/devices/{deviceId}/revoke", UriKind.Relative), null, TestContext.Current.CancellationToken);
        using var stopped = await device.GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);
        using var again = await client.PostAsync(new Uri($"/v1/admin/devices/{deviceId}/revoke", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var list = await client.GetFromJsonAsync<TechniciansResponse>(new Uri("/v1/admin/technicians", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stopped.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(0, list!.Seats.Used);
        Assert.NotNull(Assert.Single(Assert.Single(list.Technicians).Devices).RevokedAt);
    }

    [Fact]
    public async Task AnotherTenantsDeviceCannotBeRevokedAndLooksLikeNoDevice()
    {
        var mine = await api.SeedOwnerAsync();
        var theirs = await api.SeedOwnerAsync("Birch Legal");
        var (_, theirDevice, theirToken) = await api.SeedDeviceAsync(theirs.TenantId);
        using var client = api.Owner(mine);

        using var response = await client.PostAsync(new Uri($"/v1/admin/devices/{theirDevice}/revoke", UriKind.Relative), null, TestContext.Current.CancellationToken);
        using var unknown = await client.PostAsync(new Uri($"/v1/admin/devices/{Guid.NewGuid()}/revoke", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(await api.UseAsync(db => db.Devices.Where(d => d.Id == theirDevice).Select(d => d.RevokedAt).SingleAsync()));
        using var stillWorks = await api.Device(theirToken).GetAsync(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
    }

    [Fact]
    public async Task ThePolicyIsReadAndSetForThisTenantOnly()
    {
        var mine = await api.SeedOwnerAsync();
        var theirs = await api.SeedOwnerAsync("Birch Legal");
        _ = await api.UseAsync(db => Policies.SetAsync(db, theirs.TenantId, 30, false, false, false, TimeProvider.System));
        using var client = api.Owner(mine);

        var defaults = await client.GetFromJsonAsync<TenantPolicyResponse>(new Uri("/v1/admin/policy", UriKind.Relative), TestContext.Current.CancellationToken);
        using var put = await client.PutAsJsonAsync(new Uri("/v1/admin/policy", UriKind.Relative), Policy(retention: 14, channel: "pilot", hours: 6), TestContext.Current.CancellationToken);
        var read = await client.GetFromJsonAsync<TenantPolicyResponse>(new Uri("/v1/admin/policy", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(Policies.DefaultVersion, defaults!.Version);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(14, read!.RetentionDays);
        Assert.Equal("pilot", read.UpdateChannel);
        Assert.Equal(6, read.UpdateCheckHours);
        Assert.Equal(30, (await api.UseAsync(db => Policies.LatestAsync(db, theirs.TenantId)))!.RetentionDays);
        Assert.Equal(1, await api.UseAsync(db => db.Policies.CountAsync(p => p.TenantId == theirs.TenantId)));
    }

    [Fact]
    public async Task APolicyWithAFieldLeftOutIsRefusedRatherThanReadAsOff()
    {
        // A page that forgot localOnlyLocked must not unlock a locked local-only (INV-11).
        var owner = await api.SeedOwnerAsync();
        using var client = api.Owner(owner);
        _ = await client.PutAsJsonAsync(new Uri("/v1/admin/policy", UriKind.Relative), Policy(localOnly: true, locked: true), TestContext.Current.CancellationToken);

        using var partial = await client.PutAsync(
            new Uri("/v1/admin/policy", UriKind.Relative),
            new StringContent("""{"retentionDays":7,"localOnly":true,"captureAllWindows":false,"updateChannel":"stable","updateChannelLocked":false,"updateCheckHours":24,"updateChecksUnderLocalOnly":false}""", System.Text.Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var kept = await client.GetFromJsonAsync<TenantPolicyResponse>(new Uri("/v1/admin/policy", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
        Assert.True(kept!.LocalOnlyLocked);
    }

    [Theory]
    [InlineData(0, "stable", 24)]
    [InlineData(366, "stable", 24)]
    [InlineData(7, "beta", 24)]
    [InlineData(7, "stable", -1)]
    [InlineData(7, "stable", 169)]
    public async Task APolicyOutsideItsRangesIsRefusedNotClamped(int retention, string channel, int hours)
    {
        var owner = await api.SeedOwnerAsync();
        using var client = api.Owner(owner);

        using var response = await client.PutAsJsonAsync(new Uri("/v1/admin/policy", UriKind.Relative), Policy(retention: retention, channel: channel, hours: hours), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await api.UseAsync(db => db.Policies.CountAsync(p => p.TenantId == owner.TenantId)));
    }

    [Fact]
    public async Task TwoSavesInTheSameMillisecondBothLandAndTheSecondIsTheLatest()
    {
        // A double click on Save. The version is the time to the millisecond, so the second row is put
        // a millisecond on rather than refused by the unique index as a 500.
        var owner = await api.SeedOwnerAsync();
        var at = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero).AddTicks(4_000);
        var first = await api.UseAsync(db => Policies.SetAsync(db, owner.TenantId, 7, false, false, false, new Held(at)));
        var second = await api.UseAsync(db => Policies.SetAsync(db, owner.TenantId, 9, false, false, false, new Held(at.AddTicks(5_000))));

        Assert.NotEqual(first.Version, second.Version);
        Assert.Equal(second.Version, (await api.UseAsync(db => Policies.LatestAsync(db, owner.TenantId)))!.Version);
    }

    [Fact]
    public async Task ASaveThatLosesTheRaceForItsVersionGoesAgainOnce()
    {
        // Another save lands the same version between this one reading the latest and writing: the unique
        // index refuses this one, and it goes again from the new latest instead of answering 500.
        var owner = await api.SeedOwnerAsync();
        var racer = new ConcurrentSave(api.Services, times: 1);
        using var host = api.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<ScreenTailContext>(o => o.AddInterceptors(racer))));
        using var client = Owner(host, owner);

        using var response = await client.PutAsJsonAsync(new Uri("/v1/admin/policy", UriKind.Relative), Policy(retention: 4), TestContext.Current.CancellationToken);
        var saved = await response.Content.ReadFromJsonAsync<TenantPolicyResponse>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, saved!.RetentionDays);
        Assert.Equal(2, await api.UseAsync(db => db.Policies.CountAsync(p => p.TenantId == owner.TenantId)));
        Assert.Equal(saved.Version, (await api.UseAsync(db => Policies.LatestAsync(db, owner.TenantId)))!.Version);
        Assert.NotEqual(racer.Taken.Single(), saved.Version);
    }

    [Fact]
    public async Task ASaveThatLosesTheRaceTwiceIsAConflictNotAServerError()
    {
        var owner = await api.SeedOwnerAsync();
        var racer = new ConcurrentSave(api.Services, times: 2);
        using var host = api.WithWebHostBuilder(b => b.ConfigureServices(s => s.ConfigureDbContext<ScreenTailContext>(o => o.AddInterceptors(racer))));
        using var client = Owner(host, owner);

        using var response = await client.PutAsJsonAsync(new Uri("/v1/admin/policy", UriKind.Relative), Policy(retention: 4), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("policy_conflict", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    private static HttpClient Owner(WebApplicationFactory<Program> host, OwnerSession session)
    {
        var client = host.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-screentail-admin={session.Token}");
        client.DefaultRequestHeaders.Add("X-ScreenTail-CSRF", session.Csrf);
        return client;
    }

    /// <summary>
    /// Another writer, landing a policy with the same version just before this save, the given number of
    /// times. It writes through the suite's own host, which has no interceptor, so it never races itself.
    /// </summary>
    private sealed class ConcurrentSave(IServiceProvider other, int times) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private int _left = times;

        public List<string> Taken { get; } = [];

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(eventData);
            var adding = eventData.Context!.ChangeTracker.Entries<Policy>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).FirstOrDefault();
            if (adding is not null && _left > 0)
            {
                _left--;
                using var scope = other.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ScreenTailContext>();
                db.Policies.Add(new Policy { Id = Guid.NewGuid(), TenantId = adding.TenantId, Version = adding.Version, CreatedAt = adding.CreatedAt });
                _ = await db.SaveChangesAsync(cancellationToken);
                Taken.Add(adding.Version);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class Held(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task CapturingEveryWindowNeedsConfirmingWhenItIsTurnedOn()
    {
        // INV-5: the remote tools and the admin allowlist by default; every window is opt-in and confirmed.
        var owner = await api.SeedOwnerAsync();
        using var client = api.Owner(owner);

        using var unconfirmed = await client.PutAsJsonAsync(new Uri("/v1/admin/policy", UriKind.Relative), Policy(allWindows: true), TestContext.Current.CancellationToken);
        using var confirmed = await client.PutAsJsonAsync(new Uri("/v1/admin/policy", UriKind.Relative), Policy(allWindows: true, confirm: true), TestContext.Current.CancellationToken);
        using var kept = await client.PutAsJsonAsync(new Uri("/v1/admin/policy", UriKind.Relative), Policy(allWindows: true, retention: 5), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Contains("all_windows_unconfirmed", await unconfirmed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    [Fact]
    public async Task IntegrationStatusSaysWhatIsConnectedAndHowItWentAndNeverACredential()
    {
        var mine = await api.SeedOwnerAsync();
        var theirs = await api.SeedOwnerAsync("Birch Legal");
        var checkedAt = DateTimeOffset.UtcNow;
        _ = await api.UseAsync(async db =>
        {
            db.Integrations.Add(new Integration { Id = Guid.NewGuid(), TenantId = mine.TenantId, Provider = "connectwise", SiteUrl = "https://na.myconnectwise.example", SecretHint = "WXYZ", SecretCiphertext = [1, 2, 3], ConnectedAt = checkedAt, LastCheckedAt = checkedAt, LastError = "ConnectWise refused the key." });
            db.Integrations.Add(new Integration { Id = Guid.NewGuid(), TenantId = theirs.TenantId, Provider = "hudu", SiteUrl = "https://birch.huducloud.example", SecretHint = "ABCD", ConnectedAt = checkedAt, LastCheckedAt = checkedAt });
            return await db.SaveChangesAsync();
        });
        using var client = api.Owner(mine);

        var body = await client.GetStringAsync(new Uri("/v1/admin/integrations", UriKind.Relative), TestContext.Current.CancellationToken);
        var status = System.Text.Json.JsonSerializer.Deserialize<AdminIntegrationsResponse>(body, System.Text.Json.JsonSerializerOptions.Web);

        var cw = Assert.Single(status!.Integrations, i => i.Provider == "connectwise");
        var hudu = Assert.Single(status.Integrations, i => i.Provider == "hudu");
        Assert.True(cw.Connected);
        Assert.Equal("failed", cw.LastResult);
        Assert.Equal("ConnectWise refused the key.", cw.LastError);
        Assert.False(hudu.Connected);
        Assert.Null(hudu.SiteUrl);
        Assert.DoesNotContain("WXYZ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("birch", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoDownloadConfiguredThePageSaysSo()
    {
        var owner = await api.SeedOwnerAsync();
        using var client = api.Owner(owner);

        var download = await client.GetFromJsonAsync<DownloadResponse>(new Uri("/v1/admin/download", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Null(download!.Url);
        Assert.Contains("No client download", download.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfiguredDownloadLinkIsGiven()
    {
        using var host = api.WithWebHostBuilder(b => b.UseSetting("Admin:ClientDownloadUrl", "https://downloads.screentail.example/ScreenTail.msi"));
        var owner = await api.SeedOwnerAsync();
        using var client = host.CreateClient(new() { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-screentail-admin={owner.Token}");

        var download = await client.GetFromJsonAsync<DownloadResponse>(new Uri("/v1/admin/download", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("https://downloads.screentail.example/ScreenTail.msi", download!.Url);
    }

    private static UpdatePolicyRequest Policy(int retention = 7, bool localOnly = false, bool locked = false, bool allWindows = false, bool confirm = false, string channel = "stable", int hours = 24) =>
        new(retention, localOnly, locked, allWindows, confirm, channel, false, hours, false);
}
