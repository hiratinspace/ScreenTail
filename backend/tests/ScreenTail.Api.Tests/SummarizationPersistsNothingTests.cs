using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests;

/// <summary>
/// INV-7: the backend never persists a capture.
///
/// The invariant is the reason an MSP can let a session leave the machine at all (the frames are held in
/// memory for one request and let go), and it is exactly the kind of claim that stays true until someone
/// adds a cache "just for retries". So it is asserted rather than documented: the database is counted
/// before and after a summarization request, row by row across every table, and nothing may have
/// appeared but the one cost row the ledger is for.
///
/// The request goes the whole way (2026-10-12 review, T-208). These tests used to run on a host with no
/// drafting key, so every request stopped at "not configured" before any part of the drafting path ran,
/// and the tables were a list kept by hand that had already missed two. Now the host has a key and a
/// stubbed model, the draft comes back, and the tables are read from the model: a table added tomorrow
/// is counted without anybody remembering to.
///
/// Counting rows rather than bytes on purpose. A byte count moves for reasons that have nothing to do
/// with what was stored (a page split, a vacuum, an autoincrement), and a test that fails for those is
/// a test somebody turns off. Beside the counts, every value in every table is searched for the bundle's
/// text and image, so a capture written into a row that was going to change anyway is found too.
/// </summary>
public sealed class SummarizationPersistsNothingTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Ocr = "Services  Print Spooler  Stopped  Automatic";

    private const string Said = "the spooler service is stopped, so nothing is going to print";

    // Eight bytes of JPEG header, as the shared contract fixture carries.
    private const string Image = "/9j/4AAQSkZJRg==";

    private const string Drafted = "Found the Print Spooler service stopped and set it to automatic.";

    [Fact]
    public async Task ASummarizationRequestLeavesTheDatabaseExactlyAsItWas()
    {
        var model = new StubModel();
        using var host = Drafting(model);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        var before = await CountEverythingAsync();

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            Bundle("s-1"),
            TestContext.Current.CancellationToken);
        var answer = await response.Content.ReadFromJsonAsync<SummarizeResponse>(TestContext.Current.CancellationToken);

        var after = await CountEverythingAsync();

        // The drafting path ran: the model was asked, with the frame, and the note came back.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", answer!.Status);
        Assert.Equal(1, model.Calls);
        Assert.Contains(Image, model.Body, StringComparison.Ordinal);

        // And nothing of it stayed: one cost row, which is a tenant, a session id, a name and a number.
        Assert.Equal(Expected(before, draftCosts: 1), after);
        await AssertNoCaptureAnywhereAsync();
    }

    [Fact]
    public async Task ManyRequestsStillLeaveItEmpty()
    {
        // One request proves nothing about a leak that only shows up under repetition, which is what a
        // cache keyed by session id would look like.
        var model = new StubModel();
        using var host = Drafting(model);
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(host, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);
        var before = await CountEverythingAsync();

        for (var i = 0; i < 25; i++)
        {
            using var response = await client.PostAsJsonAsync(
                new Uri("/v1/sessions/summarize", UriKind.Relative),
                Bundle($"s-{i}"),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(25, model.Calls);
        Assert.Equal(Expected(before, draftCosts: 25), await CountEverythingAsync());
        await AssertNoCaptureAnywhereAsync();
    }

    [Fact]
    public async Task EveryTableInTheModelIsCounted()
    {
        // The list the hand-kept version missed, named so a reader can see the enumeration reaches them.
        var tables = await CountEverythingAsync();

        Assert.Contains("company_mappings", tables.Keys);
        Assert.Contains("invites", tables.Keys);
        Assert.Contains("draft_costs", tables.Keys);
        Assert.Equal(typeof(ScreenTailContext).GetProperties().Count(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>)), tables.Count);
    }

    [Fact]
    public async Task SummarizingWithoutATokenIsRefused()
    {
        using var client = api.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            Bundle("s-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARequestWithNoSessionIdIsRejected()
    {
        var (tenantId, userId, deviceId) = await SeedAsync();
        using var client = Client(api, api.Issuer.ForDevice(tenantId, userId, deviceId).Token);

        using var response = await client.PostAsJsonAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            Bundle("  "),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Dictionary<string, long> Expected(Dictionary<string, long> before, int draftCosts)
    {
        var expected = new Dictionary<string, long>(before, StringComparer.Ordinal);
        expected["draft_costs"] += draftCosts;
        return expected;
    }

    /// <summary>Every row in every table the model maps. If a capture is ever written, one of these moves.</summary>
    private Task<Dictionary<string, long>> CountEverythingAsync() => api.UseAsync(async db =>
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in Tables(db))
        {
            await using var command = await CommandAsync(db, $"SELECT COUNT(*) FROM \"{table}\"");
            counts[table] = Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        return counts;
    });

    /// <summary>Every value of every row, searched for what the bundle carried and what the model wrote.</summary>
    private async Task AssertNoCaptureAnywhereAsync()
    {
        var image = Convert.FromBase64String(Image);
        var found = await api.UseAsync(async db =>
        {
            var hits = new List<string>();
            foreach (var table in Tables(db))
            {
                await using var command = await CommandAsync(db, $"SELECT * FROM \"{table}\"");
                await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    for (var column = 0; column < reader.FieldCount; column++)
                    {
                        var value = reader.GetValue(column);
                        var leaked = value switch
                        {
                            string text => new[] { Ocr, Said, Image, Drafted, "Print Spooler" }.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase)),
                            byte[] bytes => bytes.AsSpan().IndexOf(image) >= 0 || Encoding.UTF8.GetString(bytes).Contains("Print Spooler", StringComparison.OrdinalIgnoreCase),
                            _ => false,
                        };
                        if (leaked)
                        {
                            hits.Add($"{table}.{reader.GetName(column)}");
                        }
                    }
                }
            }

            return hits;
        });

        Assert.Empty(found);
    }

    private static IEnumerable<string> Tables(ScreenTailContext db) =>
        db.Model.GetEntityTypes().Select(entity => entity.GetTableName()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);

    private static async Task<DbCommand> CommandAsync(ScreenTailContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        var command = connection.CreateCommand();
#pragma warning disable CA2100 // The table names come from the EF model, not from input.
        command.CommandText = sql;
#pragma warning restore CA2100
        return command;
    }

    /// <summary>The fixture's host with a drafting key and the model stubbed, so the request is drafted rather than refused.</summary>
    private WebApplicationFactory<Program> Drafting(StubModel model) => api.WithWebHostBuilder(builder =>
    {
        _ = builder.UseSetting("Summarization:ApiKey", "not-a-real-key");
        _ = builder.ConfigureTestServices(services => services.Configure<HttpClientFactoryOptions>(
            nameof(GeminiProvider),
            options => options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = model)));
    });

    /// <summary>A session as the client sends it. Nothing in it may reach a table.</summary>
    private static SummarizeBundle Bundle(string sessionId) => new()
    {
        SessionId = sessionId,
        DurationMs = 12 * 60 * 1000,
        Frames = [new BundleFrame("f-0001", 1_000, Ocr) { Image = Image }],
        Transcript = [new BundleSegment("t-0001", 1_200, Said)],
    };

    private static HttpClient Client(WebApplicationFactory<Program> host, string token)
    {
        var client = host.CreateClient();
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

    /// <summary>Answers every call with a note that cites the bundle's frame and segment, and keeps what it was asked.</summary>
    private sealed class StubModel : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {
                      "candidates": [ { "content": { "parts": [ { "text": {{JsonSerializer.Serialize(Draft())}} } ] } } ],
                      "usageMetadata": { "promptTokenCount": 10, "candidatesTokenCount": 10 }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        private static string Draft() => $$"""
            {"problem": "Nothing would print.",
             "steps": [{"text": "{{Drafted}}", "confidence": "high",
                        "frame_refs": ["f-0001"], "transcript_refs": ["t-0001"]}],
             "result": "Printing works again.", "follow_ups": [], "suggested_title": "Printer offline",
             "suggested_time_minutes": 12, "kb_candidate": false, "kb_reason": "Routine.",
             "source": "cloud", "prompt_version": "note_v1"}
            """;
    }
}
