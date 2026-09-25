using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Tenancy;

namespace ScreenTail.Api.Tests.Tenancy;

/// <summary>
/// ST-010's device side: an invite's code activates a device and binds it to the tenant (AC1), a seat
/// is counted per active device and the one past the limit is refused in the words the spec gives
/// (AC2), a refresh token buys access tokens until the device is revoked, and offboarding leaves no row
/// of the tenant behind (AC4's deletion; the receipt email waits for a mail provider).
/// </summary>
public sealed class ActivationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task AnInviteCodeActivatesADeviceAndBindsItToTheTenant()
    {
        var (tenantId, invite) = await InviteAsync(seats: 5, "t.ortiz@acme.example");
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(invite.Code, "TECH-LAPTOP"), TestContext.Current.CancellationToken);
        var activated = await response.Content.ReadFromJsonAsync<ActivatedDevice>(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Acme IT", activated!.TenantName);
        Assert.NotEmpty(activated.RefreshToken);
        Assert.NotEmpty(activated.AccessToken);
        Assert.True(activated.ExpiresAt > DateTimeOffset.UtcNow);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", activated.AccessToken);
        var me = await client.GetFromJsonAsync<MeResponse>(new Uri("/v1/me", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(tenantId, me!.Tenant.Id);
        Assert.Equal("TECH-LAPTOP", me.Device.Name);
        Assert.Equal("t.ortiz@acme.example", me.User.Email);
    }

    [Fact]
    public async Task ACodeIsOneUseAndGoesStaleAfterSeventyTwoHours()
    {
        var (tenantId, invite) = await InviteAsync(seats: 5, "a@acme.example");
        using var client = api.CreateClient();
        using var first = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(invite.Code, "ONE"), TestContext.Current.CancellationToken);
        using var again = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(invite.Code, "TWO"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("already been used", await again.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var stale = await api.UseAsync(db => Invites.CreateAsync(db, tenantId, "b@acme.example", "B", new FrozenTime(DateTimeOffset.UtcNow - Invites.Lifetime - TimeSpan.FromMinutes(1))));
        using var expired = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(stale.Code, "THREE"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Contains("expired", await expired.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        using var wrong = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest("NOPE-NOPE-NO", "FOUR"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task TheSixthDeviceOnAFiveSeatTenantIsRefusedInTheSpecsWords()
    {
        var (tenantId, _) = await InviteAsync(seats: 5, "first@acme.example");
        using var client = api.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var invite = await api.UseAsync(db => Invites.CreateAsync(db, tenantId, $"tech{i}@acme.example", $"Tech {i}", TimeProvider.System));
            using var ok = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(invite.Code, $"LAPTOP-{i}"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var sixth = await api.UseAsync(db => Invites.CreateAsync(db, tenantId, "tech6@acme.example", "Tech 6", TimeProvider.System));
        using var refused = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(sixth.Code, "LAPTOP-6"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Seat limit reached — contact your admin", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(5, await api.UseAsync(db => db.Devices.CountAsync(d => d.TenantId == tenantId)));
    }

    [Fact]
    public async Task ARefreshTokenBuysAccessTokensUntilTheDeviceIsRevoked()
    {
        var (_, invite) = await InviteAsync(seats: 1, "r@acme.example");
        using var client = api.CreateClient();
        using var activation = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(invite.Code, "R"), TestContext.Current.CancellationToken);
        var activated = (await activation.Content.ReadFromJsonAsync<ActivatedDevice>(TestContext.Current.CancellationToken))!;

        using var refreshed = await client.PostAsJsonAsync(new Uri("/v1/devices/token", UriKind.Relative), new RefreshRequest(activated.RefreshToken), TestContext.Current.CancellationToken);
        var access = await refreshed.Content.ReadFromJsonAsync<AccessTokenResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotEmpty(access!.AccessToken);

        _ = await api.UseAsync(async db =>
        {
            var device = await db.Devices.SingleAsync(d => d.Id == activated.DeviceId);
            device.RevokedAt = DateTimeOffset.UtcNow;
            return await db.SaveChangesAsync();
        });
        using var revoked = await client.PostAsJsonAsync(new Uri("/v1/devices/token", UriKind.Relative), new RefreshRequest(activated.RefreshToken), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        using var garbage = await client.PostAsJsonAsync(new Uri("/v1/devices/token", UriKind.Relative), new RefreshRequest("not-a-token"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
    }

    [Fact]
    public async Task OffboardingDeletesEveryRowOfTheTenantAndNoOther()
    {
        var (gone, invite) = await InviteAsync(seats: 2, "gone@acme.example");
        var (kept, keptInvite) = await InviteAsync(seats: 2, "kept@birch.example");
        using var client = api.CreateClient();
        using var one = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(invite.Code, "GONE-1"), TestContext.Current.CancellationToken);
        using var two = await client.PostAsJsonAsync(new Uri("/v1/devices/activate", UriKind.Relative), new ActivateDeviceRequest(keptInvite.Code, "KEPT-1"), TestContext.Current.CancellationToken);
        _ = await api.UseAsync(async db =>
        {
            db.Integrations.Add(new Integration { Id = Guid.NewGuid(), TenantId = gone, Provider = "hudu", SiteUrl = "https://x.example", ConnectedAt = DateTimeOffset.UtcNow });
            db.CompanyMappings.Add(new CompanyMapping { Id = Guid.NewGuid(), TenantId = gone, PsaCompany = "Acme", DocCompanyId = "7", DocCompanyName = "Acme", Confidence = "manual", CreatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });

        var deleted = await api.UseAsync(db => Offboarding.DeleteTenantAsync(db, gone));

        Assert.True(deleted.Rows > 0);
        Assert.Equal(0, await api.UseAsync(db => db.Tenants.CountAsync(t => t.Id == gone)));
        Assert.Equal(0, await api.UseAsync(db => db.Users.CountAsync(u => u.TenantId == gone)));
        Assert.Equal(0, await api.UseAsync(db => db.Devices.CountAsync(d => d.TenantId == gone)));
        Assert.Equal(0, await api.UseAsync(db => db.Invites.CountAsync(i => i.TenantId == gone)));
        Assert.Equal(0, await api.UseAsync(db => db.Integrations.CountAsync(i => i.TenantId == gone)));
        Assert.Equal(0, await api.UseAsync(db => db.CompanyMappings.CountAsync(m => m.TenantId == gone)));
        Assert.Equal(1, await api.UseAsync(db => db.Devices.CountAsync(d => d.TenantId == kept)));
        Assert.Equal(1, await api.UseAsync(db => db.Tenants.CountAsync(t => t.Id == kept)));
    }

    private async Task<(Guid TenantId, IssuedInvite Invite)> InviteAsync(int seats, string email)
    {
        var tenantId = Guid.NewGuid();
        var invite = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = email.EndsWith("birch.example", StringComparison.Ordinal) ? "Birch Legal" : "Acme IT", Seats = seats, CreatedAt = DateTimeOffset.UtcNow });
            _ = await db.SaveChangesAsync();
            return await Invites.CreateAsync(db, tenantId, email, "A Technician", TimeProvider.System);
        });
        return (tenantId, invite);
    }

    private sealed class FrozenTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
