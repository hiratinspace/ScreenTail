using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// Who may spend a tenant's drafting budget (ST-063; 2026-09-19 review).
///
/// The review found that <c>/v1/sessions/summarize</c> read one claim and asked the database nothing. A
/// revoked laptop already got a 401 from <c>/v1/me</c> and went on drafting here until its token
/// expired — against the tenant's own budget, at the tenant's own expense. Both the README and the
/// model's comments said revocation was checked on every request.
///
/// These are the three cases <c>MeEndpointTests</c> has always covered, asked of the endpoint that
/// spends money. Each would pass against an endpoint that checks nothing but the signature, which is
/// exactly why the old one did.
///
/// They assert 401 rather than 501: the refusal has to come before "is drafting configured", or a
/// deployment with a provider key would answer a revoked device differently from one without.
/// </summary>
public sealed class SummarizeAuthorizationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task ARevokedDeviceMaySpendNothing()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        var token = api.Issuer.ForDevice(tenantId, userId, deviceId).Token;
        _ = await api.UseAsync(db => db.Devices
            .Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow)));

        Assert.Equal(HttpStatusCode.Unauthorized, await DraftAsync(token));
    }

    [Fact]
    public async Task ADisabledTechnicianMaySpendNothing()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        var token = api.Issuer.ForDevice(tenantId, userId, deviceId).Token;
        _ = await api.UseAsync(db => db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.DisabledAt, DateTimeOffset.UtcNow)));

        Assert.Equal(HttpStatusCode.Unauthorized, await DraftAsync(token));
    }

    [Fact]
    public async Task ADisabledTenantMaySpendNothing()
    {
        // The one that costs real money: an MSP that stopped paying, still drafting at our expense.
        var (tenantId, userId, deviceId) = await SeedAsync();
        var token = api.Issuer.ForDevice(tenantId, userId, deviceId).Token;
        _ = await api.UseAsync(db => db.Tenants
            .Where(t => t.Id == tenantId)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.DisabledAt, DateTimeOffset.UtcNow)));

        Assert.Equal(HttpStatusCode.Unauthorized, await DraftAsync(token));
    }

    [Fact]
    public async Task ADeviceThatWasNeverActivatedMaySpendNothing()
    {
        // A token signed with the right key naming a device that is not in the database. Only a lookup
        // can tell the difference, and the old endpoint did not do one.
        var (tenantId, userId, _) = await SeedAsync();
        var token = api.Issuer.ForDevice(tenantId, userId, Guid.NewGuid()).Token;

        Assert.Equal(HttpStatusCode.Unauthorized, await DraftAsync(token));
    }

    [Fact]
    public async Task ADeviceThatIsStillGoodGetsPastAuthorization()
    {
        // The control. Without it, an endpoint that refused everybody would pass all of the above.
        // 501 because this deployment has no provider key, which is the answer after authorization.
        var (tenantId, userId, deviceId) = await SeedAsync();
        var token = api.Issuer.ForDevice(tenantId, userId, deviceId).Token;

        Assert.Equal(HttpStatusCode.NotImplemented, await DraftAsync(token));
    }

    [Fact]
    public async Task AHostileBundleIsRefusedBeforeAnythingElseIsAsked()
    {
        // A session id longer than the ledger's column: billed by the model, refused by Postgres, and
        // never counted against the cap. It costs nothing to refuse and cost real money to accept.
        var (tenantId, userId, deviceId) = await SeedAsync();
        var token = api.Issuer.ForDevice(tenantId, userId, deviceId).Token;

        Assert.Equal(HttpStatusCode.BadRequest, await DraftAsync(token, new string('x', 65)));
    }

    [Fact]
    public async Task ARevokedDeviceIsTurnedAwayBeforeItIsToldAnythingAboutTheRules()
    {
        // 2026-09-20 review. The bundle was checked first, so a revoked laptop -- or anyone holding a
        // token from one -- could send deliberately malformed bundles and read the limits back out of
        // the 400s: the frame ceiling, the identifier rule, which media types are accepted, how much
        // text is too much. Each answer is small; together they are a map of the endpoint, handed to
        // the one caller already established as not allowed to be here.
        //
        // Who you are is the first question. What you sent is only interesting once the answer is "a
        // device that may draft".
        var (tenantId, userId, deviceId) = await SeedAsync();
        var token = api.Issuer.ForDevice(tenantId, userId, deviceId).Token;
        _ = await api.UseAsync(db => db.Devices
            .Where(d => d.Id == deviceId)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow)));

        Assert.Equal(HttpStatusCode.Unauthorized, await DraftAsync(token, sessionId: "not a valid identifier!"));
    }

    private async Task<HttpStatusCode> DraftAsync(string token, string sessionId = "s-1")
    {
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            new SummarizeBundle
            {
                SessionId = sessionId,
                DurationMs = 12 * 60 * 1000,
                Frames = [new BundleFrame("f1", 1_000, "Services Print Spooler Stopped") { Image = "aW1hZ2U=" }],
                Transcript = [new BundleSegment("t1", 1_200, "clearing the queue now")],
            },
            TestContext.Current.CancellationToken);

        return response.StatusCode;
    }

    private async Task<(Guid TenantId, Guid UserId, Guid DeviceId)> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Spender", Seats = 1, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId}@example.com", DisplayName = "T", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device
            {
                Id = deviceId,
                TenantId = tenantId,
                UserId = userId,
                Name = "LAPTOP",
                TokenHash = DeviceTokens.Hash(DeviceTokens.Create()),
                ActivatedAt = DateTimeOffset.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

        return (tenantId, userId, deviceId);
    }
}
