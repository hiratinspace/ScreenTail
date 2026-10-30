using System.Diagnostics;
using System.Globalization;
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
/// it was read as 10.8 million code points, held in lists three times over. And canonical ordering sorted
/// a run of marks by insertion, which ten thousand marks of two classes in descending order make
/// quadratic.
///
/// Each shape is the most text BundleLimits accepts, of one character, through
/// <see cref="DraftValidator.Check"/>, with a draft that quotes the transcript and a result of the same
/// character. Time and allocation are the least of three runs. Measured on the development machine (an
/// M-series Mac), before the bound and after it, in a Release build as CI runs it, Debug in brackets:
///
/// U+FDFA: 126 ms (473) and 286 MB, read and passed; now 1 ms (6) and 1.2 MB, refused.
/// U+3392: 22 ms (83) and 39 MB; now 27 ms (107) and 8.2 MB, passed both times.
/// U+FB4F: 15 ms (57) and 22 MB; now 13 ms (55 to 89) and 5.9 MB, passed both times.
/// The run of marks: 3,343 ms (7,154) and 10 MB; now 6 ms (37) and 3.5 MB, passed both times.
/// The endpoint: 200 and the draft in 503 ms (594); now 422 and 209 characters in 190 ms (162).
///
/// The budgets are 16 MB a check, and a bound on the work canonical ordering does. Neither depends on
/// the machine, so neither can fail because the machine is busy: 16 MB is twice the most measured now and
/// under what each ligature cost before, and U+FDFA before is caught by its verdict as well as its
/// allocation. The run of marks before allocated only 10 MB and was caught by time alone, until a 1 s
/// budget on a loaded machine failed a check that takes 6 ms ("Checking took 1009 ms", 2026-10-29). Its
/// cost was the moves of an insertion sort, so the moves are what is bounded now: at most
/// <see cref="MovesPerCharacter"/> for each character checked, 10.1 million for the 632,408 here. The run
/// of marks moves 632,411 now; sorted by insertion again, it moves 883 million and fails the bound
/// eighty-seven times over.
///
/// The time a check takes is still measured, in <see cref="ReadingBoundTimingTests"/>, which runs alone
/// and allows far more than a check needs: a backstop for a cost nothing here counts.
/// </summary>
public sealed class ReadingBoundTests
{
    /// <summary>More than the model's output cap could carry: 8,192 tokens of one character each, four times over.</summary>
    private const int DraftChars = 32_768;

    private const long CheckAllocationBudget = 16L * 1024 * 1024;

    /// <summary>
    /// Code points canonical ordering may move for each character checked. Sixteen is half of
    /// <c>DraftReading.InsertionRun</c>: a run short enough to be sorted by insertion moves each of its
    /// marks fewer times than that on average, and a longer run moves each once. The run of marks is the
    /// only text here with marks to move, and each of its characters is one mark, so the bound holds
    /// whatever their order.
    /// </summary>
    private const int MovesPerCharacter = 16;

    [Theory]
    [InlineData("\uFDFA", true)] // ARABIC LIGATURE SALLALLAHOU ALAYHE WASALLAM: eighteen code points.
    [InlineData("\u3392", false)] // SQUARE MHZ: three.
    [InlineData("\uFB4F", false)] // HEBREW LIGATURE ALEF LAMED: two.
    [InlineData("\u0301\u0323", false)] // Marks of classes 230 and 220 in the order canonical ordering reverses.
    public void TheLargestBundleOfOneCharacterIsCheckedWithinBudget(string unit, bool refused)
    {
        var bundle = Largest(unit);
        Assert.Null(BundleLimits.Check(bundle));
        Assert.NotNull(BundleLimits.Check(bundle with { Transcript = [.. bundle.Transcript, new BundleSegment("t-9999", 0, "x")] }));

        var draft = Draft(unit);
        var (bytes, reasons) = LeastAllocated(() => DraftValidator.Check(draft, bundle));
        var before = DraftReading.OrderingMoves;
        _ = DraftValidator.Check(draft, bundle);
        var moves = DraftReading.OrderingMoves - before;
        var characters = bundle.Transcript.Sum(s => (long)s.Text.Length) + draft.Result!.Length;

        TestContext.Current.TestOutputHelper?.WriteLine($"U+{(int)unit[0]:X4}: {bytes / 1024.0 / 1024.0:F1} MB, {moves:N0} moves for {characters:N0} characters, {reasons.Count} reasons: {string.Join(" ", reasons)}");
        Assert.Equal(refused, reasons.Count > 0);
        Assert.True(bytes < CheckAllocationBudget, $"Checking allocated {bytes / 1024 / 1024} MB; the budget is {CheckAllocationBudget / 1024 / 1024}.");
        Assert.True(moves <= MovesPerCharacter * characters, $"Canonical ordering moved {moves:N0} code points for {characters:N0} characters; the budget is {MovesPerCharacter} a character.");

        // And a floor, so a count that stopped counting cannot pass the bound by saying nothing. Every
        // mark of the run is in a run longer than InsertionRun, so each is placed once by counting; text
        // with no marks has nothing to move.
        if (unit.All(c => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark))
        {
            Assert.True(moves >= characters, $"Canonical ordering moved {moves:N0} code points for {characters:N0} marks; each has to be placed at least once.");
        }
        else
        {
            Assert.Equal(0, moves);
        }
    }

    [Theory]
    // A is U+0301 (combining class 230), D is U+0323 (220), after an "a". Canonical ordering puts D first.
    [InlineData("AD", 1)] // Reversed: one step.
    [InlineData("DA", 0)] // Already in order.
    [InlineData("ADADADADADADADADADADADADADADADAD", 136)] // 32 marks, by insertion: one step for each A before each D, 1 + 2 + ... + 16.
    [InlineData("ADADADADADADADADADADADADADADADADA", 33)] // 33 marks, by counting: each placed once.
    public void CanonicalOrderingCountsEveryMoveItMakes(string marks, long expected)
    {
        // The bound above trusts this count, so the count is checked on runs whose moves are known.
        var text = "a" + marks.Replace('A', (char)0x0301).Replace('D', (char)0x0323);

        var before = DraftReading.OrderingMoves;
        _ = DraftReading.Read(text);

        Assert.Equal(expected, DraftReading.OrderingMoves - before);
    }

    [Fact]
    public void ATextThatGrowsPastTheBoundIsRefusedRatherThanRead()
    {
        // Fail closed: what cannot be read cannot be checked, so it may not be shown. A quotation of a
        // transcript that cannot be read is one nobody can be shown to have said.
        var bundle = Largest("\uFDFA");
        var draft = Draft("\uFDFA");

        var reasons = DraftValidator.Check(draft, bundle);

        Assert.Contains(reasons, reason => reason.StartsWith("result ", StringComparison.Ordinal) && reason.Contains("too long", StringComparison.Ordinal));
        Assert.Contains(reasons, reason => reason.StartsWith("Step 1 quotes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSameShapeThroughTheEndpointGetsABoundedAnswer()
    {
        var answer = await PostLargestAsync();

        TestContext.Current.TestOutputHelper?.WriteLine($"{(int)answer.Status} in {answer.Took.TotalMilliseconds:F0} ms, {answer.Body.Length} characters: {answer.Body[..Math.Min(answer.Body.Length, 300)]}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, answer.Status);
        Assert.Contains("too long", answer.Body, StringComparison.Ordinal);
        Assert.True(answer.Body.Length < 2_048, $"The answer is {answer.Body.Length} characters.");
        Assert.Equal(2, answer.Calls);
    }

    /// <summary>What the summarize endpoint answered for the largest bundle of U+FDFA, how long it took, and how often it asked the model.</summary>
    internal sealed record EndpointAnswer(HttpStatusCode Status, string Body, TimeSpan Took, int Calls);

    /// <summary>The largest bundle of U+FDFA, through the summarize endpoint, to a model whose every answer is too long when read.</summary>
    internal static async Task<EndpointAnswer> PostLargestAsync()
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
            new StringContent(JsonSerializer.Serialize(Largest("\uFDFA")), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        watch.Stop();

        return new EndpointAnswer(response.StatusCode, body, watch.Elapsed, model.Calls);
    }

    /// <summary>The most transcript BundleLimits accepts: sixty segments of ten thousand characters, ids included.</summary>
    internal static SummarizeBundle Largest(string unit)
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

    internal static DraftJson Draft(string unit) => new()
    {
        Problem = "Nothing would print from the reception workstation.",
        Steps =
        [
            new DraftStepJson
            {
                Text = $"The customer said \u201C{Filled(unit, 4)}\u201D.",
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

    /// <summary>The least a check allocated over three runs, so the first run's one-time setup is not counted.</summary>
    private static (long Bytes, IReadOnlyList<string> Reasons) LeastAllocated(Func<IReadOnlyList<string>> check)
    {
        var bytes = long.MaxValue;
        IReadOnlyList<string> reasons = [];
        for (var run = 0; run < 3; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            reasons = check();
            bytes = Math.Min(bytes, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return (bytes, reasons);
    }

    /// <summary>The least time a check took over three runs.</summary>
    internal static TimeSpan LeastTime(Action check)
    {
        var time = TimeSpan.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var watch = Stopwatch.StartNew();
            check();
            watch.Stop();
            time = watch.Elapsed < time ? watch.Elapsed : time;
        }

        return time;
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
            var draft = JsonSerializer.SerializeToNode(Draft("\uFDFA"))!;
            draft["result"] = Filled("\uFDFA", 8_192);
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

/// <summary>Tests that measure wall-clock time run alone, after everything else, so the suite is not what slows them.</summary>
[CollectionDefinition(nameof(WallClock), DisableParallelization = true)]
public sealed class WallClock;

/// <summary>
/// The time the largest bundle takes to check and to answer, against budgets far above what either
/// needs. A backstop that catches only a very large slowdown, of the kind a cost nothing counts could
/// cause; not a measure of speed. It does not reliably catch the regression ST-061 fixed: the run of marks
/// sorted by insertion took 3,343 ms in Release when first measured (7,154 in Debug, the one figure over
/// five seconds), and measured again on 2026-10-29 it took 3.1 s in Debug on the development machine and
/// 806 ms on a reviewer's, both inside five seconds. That known cost is guarded by the bound on canonical
/// ordering's moves in <see cref="ReadingBoundTests"/>, and the ligatures by its allocation budget,
/// neither of which a machine's load changes.
///
/// The budgets are five seconds a check and ten for the endpoint: about fifty times the slowest Debug
/// check (107 ms) and endpoint (190 ms) measured, where the old 1 s and 3 s budgets were nine and fifteen
/// times, and the 1 s one failed once in ten runs of the suite on a loaded machine. Run alone, the only
/// load left is the machine's own.
/// </summary>
[Collection(nameof(WallClock))]
public sealed class ReadingBoundTimingTests
{
    private static readonly TimeSpan CheckBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EndpointBudget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("\uFDFA")]
    [InlineData("\u3392")]
    [InlineData("\uFB4F")]
    [InlineData("\u0301\u0323")]
    public void TheLargestBundleOfOneCharacterIsCheckedWellWithinAGenerousWallClockBudget(string unit)
    {
        var bundle = ReadingBoundTests.Largest(unit);
        var draft = ReadingBoundTests.Draft(unit);

        var time = ReadingBoundTests.LeastTime(() => _ = DraftValidator.Check(draft, bundle));

        TestContext.Current.TestOutputHelper?.WriteLine($"U+{(int)unit[0]:X4}: {time.TotalMilliseconds:F0} ms");
        Assert.True(time < CheckBudget, $"Checking took {time.TotalMilliseconds:F0} ms; the budget is {CheckBudget.TotalMilliseconds:F0}.");
    }

    [Fact]
    public async Task TheEndpointAnswersTheLargestBundleWellWithinAGenerousWallClockBudget()
    {
        var answer = await ReadingBoundTests.PostLargestAsync();

        TestContext.Current.TestOutputHelper?.WriteLine($"{(int)answer.Status} in {answer.Took.TotalMilliseconds:F0} ms");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, answer.Status);
        Assert.True(answer.Took < EndpointBudget, $"The endpoint took {answer.Took.TotalMilliseconds:F0} ms; the budget is {EndpointBudget.TotalMilliseconds:F0}.");
    }
}
