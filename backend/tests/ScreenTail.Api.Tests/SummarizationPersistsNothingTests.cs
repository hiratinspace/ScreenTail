using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Tests;

/// <summary>
/// INV-7: the backend never persists a capture.
///
/// The invariant is the reason an MSP can let a session leave the machine at all — the frames are held in
/// memory for one request and let go — and it is exactly the kind of claim that stays true until someone
/// adds a cache "just for retries". So it is asserted rather than documented: the database is counted
/// before and after a summarization request, row by row across every table, and nothing may have
/// appeared.
///
/// Counting rows rather than bytes on purpose. A byte count moves for reasons that have nothing to do
/// with what was stored — a page split, a vacuum, an autoincrement — and a test that fails for those is
/// a test somebody turns off.
/// </summary>
public sealed class SummarizationPersistsNothingTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task ASummarizationRequestLeavesTheDatabaseExactlyAsItWas()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        var before = await CountEverythingAsync();

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            new SummarizeRequest("s-1", Frames: 22, TranscriptSegments: 40, EstimatedTokens: 31_000),
            TestContext.Current.CancellationToken);

        var after = await CountEverythingAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ManyRequestsStillLeaveItEmpty()
    {
        // One request proves nothing about a leak that only shows up under repetition, which is what a
        // cache keyed by session id would look like.
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        var before = await CountEverythingAsync();

        for (var i = 0; i < 25; i++)
        {
            using var response = await client.PostAsJsonAsync(
                new Uri("/v1/sessions/summarize", UriKind.Relative),
                new SummarizeRequest($"s-{i}", 22, 40, 31_000),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(before, await CountEverythingAsync());
    }

    [Fact]
    public async Task SummarizingWithoutATokenIsRefused()
    {
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            new SummarizeRequest("s-1", 1, 1, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARequestWithNoSessionIdIsRejected()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            new SummarizeRequest("  ", 1, 1, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Every row in every table. If a capture is ever written, one of these moves.</summary>
    private Task<string> CountEverythingAsync() => api.UseAsync(async db =>
    {
        var counts = new[]
        {
            await db.Tenants.CountAsync(),
            await db.Users.CountAsync(),
            await db.Devices.CountAsync(),
            await db.Integrations.CountAsync(),
            await db.Policies.CountAsync(),
            await db.SessionMetrics.CountAsync(),
        };

        return string.Join(",", counts);
    });

    private HttpClient Client(string token)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(Guid TenantId, Guid UserId, Guid DeviceId)> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();

        _ = await api.UseAsync(async db =>
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Summarize", Seats = 1, CreatedAt = DateTimeOffset.UtcNow });
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
