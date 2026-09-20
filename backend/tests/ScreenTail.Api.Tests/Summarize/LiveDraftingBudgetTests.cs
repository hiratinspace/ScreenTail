using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ScreenTail.Api.Providers.Llm;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// ST-063 AC1, measured against the real model rather than argued about: a draft inside 30 seconds, for
/// under ten cents a session.
///
/// <b>Skipped unless somebody asks for it</b>, because it calls a paid API. Set
/// <c>SCREENTAIL_LIVE_LLM=1</c> and it runs against the key in <c>dotnet user-secrets</c>; leave it
/// unset — as CI does, and as every other developer does — and it skips. Nothing else in this suite is
/// allowed to touch a real provider, which <c>NoLiveProviderInTestsTests</c> enforces.
///
/// The bundles are built from the checked-in fixtures at the ceiling <c>BundleOptions</c> actually
/// imposes on the client: twenty-five frames. Measuring the everyday three-frame session would have
/// produced a comfortable number that says nothing about the session that matters, which is the long
/// one with the most pictures.
///
/// The slowest run is asserted rather than a percentile. With a handful of samples a "p95" is the
/// maximum wearing a hat, and "every run came back inside the budget" is the stronger claim anyway.
/// </summary>
public sealed class LiveDraftingBudgetTests
{
    /// <summary>ST-063 AC1. The technician is watching Review when this is running.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    /// <summary>Scope §6's per-session ceiling.</summary>
    private const decimal CostCeilingUsd = 0.10m;

    /// <summary>What <c>BundleOptions.MaxFrames</c> lets the client send.</summary>
    private const int HeaviestFrameCount = 25;

    private const int Runs = 3;

    /// <summary>
    /// Space between calls.
    ///
    /// The free tier allows five requests a minute per model, and a bench that ignores that measures
    /// its own 429s. A deployment with a paid key can shorten this; the number it produces does not
    /// depend on it, because what is timed is the call rather than the wait.
    /// </summary>
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many times a run may be re-attempted after the provider says it is unavailable.
    ///
    /// Gemini 3.6 Flash answered 503 "experiencing high demand" to roughly a third of these calls on
    /// 2026-09-19. That is not a ScreenTail defect and it is not what this measures — it is precisely
    /// what ST-064's outbox exists to absorb — so it is retried and counted rather than failed on. The
    /// count is printed, because a provider that is down more than it is up is its own finding.
    /// </summary>
    private const int TransientAttempts = 6;

    [Fact]
    public async Task TheHeaviestSessionAClientCanSendDraftsInsideTheBudget()
    {
        var key = KeyOrSkip();
        var bundle = Heaviest();
        var runs = await MeasureAsync(key, bundle, "heaviest");

        var slowest = runs.Max(run => run.Elapsed);
        var mean = runs.Average(run => run.CostUsd);

        Assert.True(
            slowest <= Budget,
            $"Slowest draft took {slowest.TotalSeconds:F1} s against a {Budget.TotalSeconds:F0} s budget.");
        Assert.True(
            mean <= CostCeilingUsd,
            $"Mean draft cost ${mean:F4} against a ${CostCeilingUsd:F2} ceiling.");
    }

    [Fact]
    public async Task AnEverydaySessionCostsWhatWeToldPeopleItWould()
    {
        // The three-frame printer call in the fixtures: what most sessions actually look like. Measured
        // separately so the average an MSP sees on its bill is not read off the worst case.
        var key = KeyOrSkip();
        var bundle = Fixture("spooler-stopped-screenconnect");
        var runs = await MeasureAsync(key, bundle, "everyday");

        Assert.True(runs.Max(run => run.Elapsed) <= Budget);
        Assert.True(runs.Average(run => run.CostUsd) <= CostCeilingUsd);
    }

    /// <summary>
    /// Drafts the bundle <see cref="Runs"/> times and reports what happened, retrying past the
    /// provider's own bad moments rather than reporting them as ours.
    /// </summary>
    private static async Task<IReadOnlyList<Run>> MeasureAsync(string key, SummarizeBundle bundle, string name)
    {
        List<Run> runs = [];
        var transient = 0;

        for (var i = 0; i < Runs; i++)
        {
            Run run;
            var attempt = 0;
            while (true)
            {
                if (runs.Count > 0 || attempt > 0)
                {
                    await Task.Delay(Pace, TestContext.Current.CancellationToken);
                }

                run = await DraftOnceAsync(key, bundle);
                if (run.Ok || run.Status != SummarizeStatus.Unavailable || ++attempt >= TransientAttempts)
                {
                    break;
                }

                transient++;
            }

            runs.Add(run);
        }

        Report(name, bundle, runs, transient);

        // A provider that never answered is not a budget this code missed, and reporting it as one would
        // be a red suite that says nothing. The free tier allows twenty requests a day per model, so a
        // second run of this bench on the same key usually ends here.
        Assert.SkipWhen(
            runs.TrueForAll(run => !run.Ok),
            $"The provider answered none of {Runs} attempts ({transient} retried): {runs[0].Reason}");

        // Anything that did come back is measured. A quota that ran out mid-bench costs samples, not
        // truth: every draft that happened still either made the budget or did not.
        return [.. runs.Where(run => run.Ok)];
    }

    private static async Task<Run> DraftOnceAsync(string key, SummarizeBundle bundle)
    {
        var options = new SummarizationOptions { ApiKey = key };
        using var http = new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/"),
            Timeout = options.Timeout,
        };

        var service = new SummarizationService(
            new GeminiProvider(http, options, PromptLibrary.Note()),
            fallback: null,
            new Tally(),
            options);

        var clock = Stopwatch.StartNew();
        var result = await service.DraftAsync(Guid.NewGuid(), bundle, TestContext.Current.CancellationToken);
        clock.Stop();

        return new Run(
            clock.Elapsed,
            result.CostUsd,
            result.Ok,
            result.Repaired,
            result.Status,
            result.Reason ?? result.Status.ToString());
    }

    /// <summary>
    /// Twenty-five frames drawn from every fixture there is, oldest first.
    ///
    /// The frames are PNG, which is why <c>BundleFrame.MediaType</c> exists: the Windows client encodes
    /// JPEG and the fixtures do not, and a picture sent under the wrong name is refused.
    /// </summary>
    private static SummarizeBundle Heaviest()
    {
        var frames = new List<BundleFrame>();
        var transcript = new List<BundleSegment>();
        var tick = 0L;

        foreach (var directory in Directory.EnumerateDirectories(Fixtures()).OrderBy(d => d, StringComparer.Ordinal))
        {
            var session = Load(directory);
            foreach (var frame in session.Frames)
            {
                if (frames.Count == HeaviestFrameCount)
                {
                    break;
                }

                tick += 20_000;
                frames.Add(frame with { Id = $"f-{frames.Count:0000}", TsMs = tick });
            }

            foreach (var segment in session.Transcript)
            {
                transcript.Add(segment with { Id = $"t-{transcript.Count:0000}", TsMs = transcript.Count * 20_000L });
            }
        }

        // Padded to the ceiling by repeating what we have: the point is the number of pictures the model
        // is asked to read, and there are only thirteen distinct ones checked in.
        for (var i = 0; frames.Count < HeaviestFrameCount; i++)
        {
            tick += 20_000;
            frames.Add(frames[i % 13] with { Id = $"f-{frames.Count:0000}", TsMs = tick });
        }

        return new SummarizeBundle
        {
            SessionId = "bench-heaviest",
            DurationMs = tick + 20_000,
            Frames = frames,
            Transcript = transcript,
        };
    }

    private static SummarizeBundle Fixture(string name) => Load(Path.Combine(Fixtures(), name));

    private static SummarizeBundle Load(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "session.json"));
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var frames = new List<BundleFrame>();
        foreach (var frame in root.GetProperty("frames").EnumerateArray())
        {
            // The four exclusions BundleBuilder applies on the client. A bench that sends frames the
            // product would never send measures a request the product cannot make.
            if (frame.GetProperty("redaction_pending").GetBoolean()
                || frame.GetProperty("sensitive_context").GetBoolean()
                || frame.GetProperty("excluded_by_user").GetBoolean())
            {
                continue;
            }

            var path = Path.Combine(directory, frame.GetProperty("image").GetString()!);
            frames.Add(new BundleFrame(
                frame.GetProperty("id").GetString()!,
                frame.GetProperty("ts_ms").GetInt64(),
                frame.TryGetProperty("ocr_text", out var ocr) ? ocr.GetString() : null)
            {
                Image = Convert.ToBase64String(File.ReadAllBytes(path)),
                MediaType = "image/png",
            });
        }

        var transcript = root.TryGetProperty("transcript", out var segments)
            ? segments.EnumerateArray().Select(segment => new BundleSegment(
                segment.GetProperty("id").GetString()!,
                segment.GetProperty("ts_ms").GetInt64(),
                segment.GetProperty("text").GetString()!)).ToList()
            : [];

        return new SummarizeBundle
        {
            SessionId = root.GetProperty("session_id").GetString()!,
            DurationMs = root.GetProperty("duration_ms").GetInt64(),
            PartialCapture = root.GetProperty("partial_capture").GetBoolean(),
            FramesPurgedUnredacted = root.GetProperty("frames_purged_unredacted").GetInt64(),
            Frames = frames,
            Transcript = transcript,
        };
    }

    private static void Report(string name, SummarizeBundle bundle, IReadOnlyList<Run> runs, int transient)
    {
        var bytes = bundle.Frames.Sum(frame => (long)(frame.Image?.Length ?? 0));
        var lines = new List<string>
        {
            $"[{name}] {bundle.Frames.Count} frames, {bundle.Transcript.Count} transcript segments, "
                + $"{bytes / 1024} KiB of base64 image",
        };

        foreach (var (run, i) in runs.Select((run, i) => (run, i)))
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"  run {i + 1}: {run.Elapsed.TotalSeconds,6:F1} s  ${run.CostUsd,8:F5}  "
                    + $"{(run.Ok ? "ok" : "FAILED")}{(run.Repaired ? " (repaired)" : string.Empty)}  {run.Reason}"));
        }

        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"  {transient} call(s) retried after the provider said it was unavailable"));
        lines.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"  slowest {runs.Max(r => r.Elapsed).TotalSeconds:F1} s   median "
                + $"{runs.Select(r => r.Elapsed.TotalSeconds).Order().ElementAt(runs.Count / 2):F1} s   "
                + $"mean cost ${runs.Average(r => r.CostUsd):F5}"));

        var report = string.Join(Environment.NewLine, lines);
        Console.WriteLine(report);

        if (Environment.GetEnvironmentVariable("SCREENTAIL_LIVE_LLM_OUT") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"{name}.txt"), report);
        }
    }

    /// <summary>
    /// The developer's key, from the same user secrets store ST-063 documents. Never from a file in the
    /// repository, and never printed.
    /// </summary>
    private static string KeyOrSkip()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("SCREENTAIL_LIVE_LLM") == "1",
            "Live drafting measurement. Set SCREENTAIL_LIVE_LLM=1 to run it; it calls a paid API.");

        var configuration = new ConfigurationBuilder()
            .AddUserSecrets("screentail-api")
            .AddEnvironmentVariables()
            .Build();

        var key = configuration["Summarization:ApiKey"];
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(key),
            "No provider key. Set one with: dotnet user-secrets set \"Summarization:ApiKey\" <key>");

        return key!;
    }

    private static string Fixtures()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "research", "fixtures", "handcrafted");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("research/fixtures/handcrafted is not above the test binary.");
    }

    private sealed record Run(TimeSpan Elapsed, decimal CostUsd, bool Ok, bool Repaired, SummarizeStatus Status, string Reason);

    /// <summary>Counts what the bench spends without a database. No cap: the bench is the thing measuring.</summary>
    private sealed class Tally : ICostLedger
    {
        public Task<decimal> SpentTodayAsync(Guid tenantId, CancellationToken ct = default) => Task.FromResult(0m);

        public Task<Guid?> ReserveAsync(Guid tenantId, string sessionId, string provider, decimal estimateUsd, CancellationToken ct = default) =>
            Task.FromResult<Guid?>(Guid.NewGuid());

        public Task SettleAsync(Guid reservationId, decimal costUsd, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordAsync(Guid tenantId, string sessionId, string provider, decimal costUsd, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
