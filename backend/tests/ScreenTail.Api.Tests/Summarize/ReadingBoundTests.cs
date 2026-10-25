using System.Diagnostics;
using System.Net;
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
using Microsoft.Extensions.Logging;
using ScreenTail.Api.Auth;
using ScreenTail.Api.Data;
using ScreenTail.Api.Endpoints;
using ScreenTail.Api.Providers;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// What reading the largest bundle a device may send costs the validator (PR #206 review).
///
/// The validator reads the transcript a device sent, not only the draft, and BundleLimits lets a bundle
/// carry 600,000 characters of it. NFKC makes U+FDFA eighteen characters, so before the bound a bundle of
/// it was read as 10.8 million code points, held in lists of boxed-size ints, three times over. And
/// canonical ordering sorted a run of marks by insertion, which a run of ten thousand marks of two
/// classes in descending order makes quadratic.
///
/// Each shape is the most text BundleLimits accepts, of one character, through
/// <see cref="DraftValidator.Check"/>, with a draft that quotes the transcript and a result of the same
/// character. The time and allocation budgets are the least of three runs; the numbers measured on the
/// development machine (an M-series Mac, Debug build) are beside each, and the budgets leave room for a
/// slower CI runner without letting the old cost back in.
/// </summary>
public sealed class ReadingBoundTests
{
    /// <summary>More than the model's output cap could carry: 8,192 tokens of one character each, four times over.</summary>
    private const int DraftChars = 32_768;

    private static readonly TimeSpan CheckBudget = TimeSpan.FromMilliseconds(1_500);
    private const long CheckAllocationBudget = 64L * 1024 * 1024;
    private static readonly TimeSpan EndpointBudget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("ﷺ", true)] // ARABIC LIGATURE SALLALLAHOU ALAYHE WASALLAM: eighteen code points.
    [InlineData("㎒", false)] // SQUARE MHZ: three.
    [InlineData("ﭏ", false)] // HEBREW LIGATURE ALEF LAMED: two.
    [InlineData("̣́", true)] // Marks of classes 230 and 220 in the order canonical ordering reverses.
    public void TheLargestBundleOfOneCharacterIsCheckedWithinBudget(string unit, bool refused)
    {
        var bundle = Largest(unit);
        Assert.Null(BundleLimits.Check(bundle));
        Assert.NotNull(BundleLimits.Check(bundle with { Transcript = [.. bundle.Transcript, new BundleSegment("t-9999", 0, "x")] }));

        var draft = Draft(unit);
        var (time, bytes, reasons) = Least(() => DraftValidator.Check(draft, bundle));

        TestContext.Current.TestOutputHelper?.WriteLine($"U+{(int)unit[0]:X4}: {time.TotalMilliseconds:F0} ms, {bytes / 1024.0 / 1024.0:F1} MB, {reasons.Count} reasons: {string.Join(" ", reasons)}");
        Assert.Equal(refused, reasons.Count > 0);
        Assert.True(time < CheckBudget, $"Checking took {time.TotalMilliseconds:F0} ms; the budget is {CheckBudget.TotalMilliseconds:F0}.");
        Assert.True(bytes < CheckAllocationBudget, $"Checking allocated {bytes / 1024 / 1024} MB; the budget is {CheckAllocationBudget / 1024 / 1024}.");
    }

    [Fact]
    public void ATextThatGrowsPastTheBoundIsRefusedRatherThanRead()
    {
        // Fail closed: what cannot be read cannot be checked, so it may not be shown. A quotation of a
        // transcript that cannot be read is one nobody can be shown to have said.
        var bundle = Largest("ﷺ");
        var draft = Draft("ﷺ");

        var reasons = DraftValidator.Check(draft, bundle);

        Assert.Contains(reasons, reason => reason.StartsWith("result ", StringComparison.Ordinal) && reason.Contains("too long", StringComparison.Ordinal));
        Assert.Contains(reasons, reason => reason.StartsWith("Step 1 quotes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSameShapeThroughTheEndpointGetsABoundedAnswer()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var model = new ExpandingModel();
        var (tenant, user, device) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        _ = builder.Logging.ClearProviders();
        _ = builder.Services.AddDbContext<ScreenTailContext>(options => options.UseSqlite(connection));
        _ = builder.Services.AddScoped(_ => new SummarizationService(model, null, new FreeLedger(), new SummarizationOptions { ApiKey = "not-a-real-key" }));
        await using var app = builder.Build();
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
        _ = app.UseDraftingConcurrency();
        _ = app.MapGroup("/v1").MapSummarize();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScreenTailContext>();
            _ = await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Bound", Seats = 1, CreatedAt = DateTimeOffset.UtcNow });
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

        var watch = Stopwatch.StartNew();
        using var response = await client.PostAsync(
            new Uri("/v1/sessions/summarize", UriKind.Relative),
            new StringContent(JsonSerializer.Serialize(Largest("ﷺ")), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        watch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"{(int)response.StatusCode} in {watch.ElapsedMilliseconds} ms, {body.Length} characters: {body[..Math.Min(body.Length, 300)]}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("too long", body, StringComparison.Ordinal);
        Assert.True(body.Length < 2_048, $"The answer is {body.Length} characters.");
        Assert.True(watch.Elapsed < EndpointBudget, $"The endpoint took {watch.ElapsedMilliseconds} ms.");
        Assert.Equal(2, model.Calls);
    }

    /// <summary>The most transcript BundleLimits accepts: sixty segments of ten thousand characters, ids included.</summary>
    private static SummarizeBundle Largest(string unit)
    {
        var segments = BundleLimits.MaxTotalTextChars / BundleLimits.MaxTextChars;
        var transcript = new List<BundleSegment>();
        for (var i = 1; i <= segments; i++)
        {
            var id = $"t-{i:D4}";
            transcript.Add(new BundleSegment(id, i * 1_000L, Filled(unit, BundleLimits.MaxTextChars - id.Length)));
        }

        return new SummarizeBundle
        {
            SessionId = "s-bound",
            DurationMs = 60 * 60 * 1000,
            Frames = [],
            Transcript = transcript,
        };
    }

    private static DraftJson Draft(string unit) => new()
    {
        Problem = "Nothing would print from the reception workstation.",
        Steps =
        [
            new DraftStepJson
            {
                Text = $"The customer said “{Filled(unit, 1_000)}”.",
                Confidence = "high",
                FrameRefs = [],
                TranscriptRefs = ["t-0001"],
            },
        ],
        Result = Filled(unit, DraftChars),
        FollowUps = [],
        SuggestedTitle = "Printer offline",
        SuggestedTimeMinutes = 10,
        KbCandidate = false,
        KbReason = "A routine fix with nothing new to write down.",
        Source = "cloud",
        PromptVersion = DraftValidator.PromptVersion,
    };

    private static string Filled(string unit, int length)
    {
        var text = new StringBuilder(length + unit.Length);
        while (text.Length < length)
        {
            _ = text.Append(unit);
        }

        return text.ToString(0, length);
    }

    private static (TimeSpan Time, long Bytes, IReadOnlyList<string> Reasons) Least(Func<IReadOnlyList<string>> check)
    {
        var time = TimeSpan.MaxValue;
        var bytes = long.MaxValue;
        IReadOnlyList<string> reasons = [];
        for (var run = 0; run < 3; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            reasons = check();
            watch.Stop();
            bytes = Math.Min(bytes, GC.GetAllocatedBytesForCurrentThread() - before);
            time = watch.Elapsed < time ? watch.Elapsed : time;
        }

        return (time, bytes, reasons);
    }

    /// <summary>A model that answers with a result eighteen times longer when read than written, every time.</summary>
    private sealed class ExpandingModel : ILlmProvider
    {
        private int _calls;

        public string Name => "expanding";

        public int Calls => Volatile.Read(ref _calls);

        public Task<ProviderResult<LlmDraft>> DraftAsync(SummarizeBundle bundle, string? repair, CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _calls);
            var draft = JsonSerializer.SerializeToNode(Draft("ﷺ"))!;
            draft["result"] = Filled("ﷺ", 8_192);
            return Task.FromResult(ProviderResult.Success(new LlmDraft(draft.ToJsonString(), 0.01m)));
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
