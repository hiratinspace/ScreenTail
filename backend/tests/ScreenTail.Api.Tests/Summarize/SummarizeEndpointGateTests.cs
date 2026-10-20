using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// The drafting endpoint refuses to draft on a host that lost the concurrency gate (2026-10-12 review,
/// E-35; review round 2 of its fixes).
///
/// The gate is one line in Program.cs, and the busy tests catch that line going missing from the real
/// host. This pins the second half: the endpoint itself checks it went through the gate, so a host that
/// maps the endpoint without it answers 500 and never calls the model, rather than drafting without a
/// limit. The same host with the gate drafts, which shows the 500 is the gate's absence and nothing else.
/// </summary>
public sealed class SummarizeEndpointGateTests
{
    [Theory]
    [InlineData(false, HttpStatusCode.InternalServerError, 0)]
    [InlineData(true, HttpStatusCode.OK, 1)]
    public async Task AHostWithoutTheGateDoesNotDraft(bool gated, HttpStatusCode status, int modelCalls)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var model = new CountingModel();
        var (tenant, user, device) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Services.AddDbContext<ScreenTailContext>(options => options.UseSqlite(connection));
        _ = builder.Services.AddScoped(_ => new SummarizationService(
            model,
            null,
            new FreeLedger(),
            new SummarizationOptions { ApiKey = "not-a-real-key" }));

        await using var app = builder.Build();

        // Who is asking, as a verified token would say it. CallerCheck still reads the device from the
        // database, so the caller is the same kind of caller the real host binds.
        _ = app.Use((context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ScreenTailClaims.TenantId, tenant.ToString()),
                    new Claim(ScreenTailClaims.UserId, user.ToString()),
                    new Claim(ScreenTailClaims.DeviceId, device.ToString()),
                ],
                "test"));
            return next(context);
        });

        if (gated)
        {
            _ = app.UseDraftingConcurrency();
        }

        _ = app.MapGroup("/v1").MapSummarize();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScreenTailContext>();
            _ = await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Gate", Seats = 1, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new User { Id = user, TenantId = tenant, Email = $"{user}@example.com", DisplayName = "T", CreatedAt = DateTimeOffset.UtcNow });
            db.Devices.Add(new Device
            {
                Id = device,
                TenantId = tenant,
                UserId = user,
                Name = "LAPTOP",
                TokenHash = DeviceTokens.Hash(DeviceTokens.Create()),
                ActivatedAt = DateTimeOffset.UtcNow,
            });
            _ = await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            new StringContent(Request(), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(modelCalls, model.Calls);
    }

    private static string Request()
    {
        using var stream = Resource("summarize-request.v1.json");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("request").GetRawText();
    }

    /// <summary>The draft the response contract's ok outcome carries, which the request above accepts.</summary>
    private static string OkDraft()
    {
        using var stream = Resource("summarize-response.v1.json");
        var document = JsonNode.Parse(stream)!;
        return document["outcomes"]!.AsArray()
            .Single(outcome => outcome!["name"]!.GetValue<string>() == "ok")!["body"]!["draft"]!
            .ToJsonString();
    }

    private static Stream Resource(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream($"ScreenTail.Api.Tests.{name}")
            ?? throw new InvalidOperationException($"The shared contract {name} is not embedded in this assembly.");

    private sealed class CountingModel : ILlmProvider
    {
        private int _calls;

        public string Name => "counting";

        public int Calls => Volatile.Read(ref _calls);

        public Task<ProviderResult<LlmDraft>> DraftAsync(SummarizeBundle bundle, string? repair, CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(ProviderResult.Success(new LlmDraft(OkDraft(), 0.01m)));
        }
    }

    private sealed class FreeLedger : ICostLedger
    {
        public Task<decimal> SpentTodayAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(0m);

        public Task<Guid?> ReserveAsync(Guid tenantId, string sessionId, string provider, decimal estimateUsd, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(Guid.NewGuid());

        public Task SettleAsync(Guid reservationId, decimal costUsd, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordAsync(Guid tenantId, string sessionId, string provider, decimal costUsd, CancellationToken ct = default) => Task.CompletedTask;
    }
}
