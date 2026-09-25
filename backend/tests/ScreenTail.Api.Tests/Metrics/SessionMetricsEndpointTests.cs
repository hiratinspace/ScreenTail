using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;

namespace ScreenTail.Api.Tests.Metrics;

/// <summary>
/// ST-098: <c>POST /v1/metrics/sessions</c> writes one row per session per tenant, replaces it on a
/// retry rather than double-counting, and accepts nothing that is content (INV-10). The weekly view
/// is Postgres SQL in the migration, checked by CI against the real database.
/// </summary>
public sealed class SessionMetricsEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task TheContractIsWhatMetricsAcceptAndARetryReplacesTheRow()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var first = await client.PostAsync(new Uri("/v1/metrics/sessions", UriKind.Relative), new StringContent(Contract("request"), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        using var again = await client.PostAsync(new Uri("/v1/metrics/sessions", UriKind.Relative), new StringContent(Contract("request").Replace("\"published\": true", "\"published\": false", StringComparison.Ordinal), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        var rows = await api.UseAsync(db => db.SessionMetrics.Where(m => m.TenantId == tenantId).ToListAsync());
        var row = Assert.Single(rows);
        Assert.Equal("s-0001", row.SessionId);
        Assert.Equal(deviceId, row.DeviceId);
        Assert.Equal(613000, row.DurationMs);
        Assert.Equal(0.5, row.EditRatio);
        Assert.False(row.Published);
    }

    [Fact]
    public void TheRequestHasNoContentFieldAtAll()
    {
        var strings = typeof(SessionMetricRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name);

        Assert.Equal(["SessionId"], strings);
    }

    [Fact]
    public async Task AMetricWithoutASessionIdIsRefused()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.PostAsync(new Uri("/v1/metrics/sessions", UriKind.Relative), new StringContent("{\"session_id\":\"\",\"started_at\":\"2026-09-25T14:02:00+00:00\",\"duration_ms\":1,\"frames\":0,\"transcript_segments\":0,\"frames_purged_unredacted\":0,\"edit_ratio\":null,\"published\":false}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

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
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme IT", Seats = 5, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = userId, TenantId = tenantId, Email = $"{userId:N}@acme.example", DisplayName = "A Technician", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device { Id = deviceId, TenantId = tenantId, UserId = userId, Name = "TECH-LAPTOP", TokenHash = DeviceTokens.Hash(DeviceTokens.Create()), ActivatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync();
        });
        return (tenantId, userId, deviceId);
    }

    private static string Contract(string property)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ScreenTail.Api.Tests.session-metric.v1.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty(property).GetRawText();
    }
}
