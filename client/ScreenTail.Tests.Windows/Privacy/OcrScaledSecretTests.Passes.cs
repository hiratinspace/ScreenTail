using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using ScreenTail.Core.Privacy;
using ScreenTail.Service.Privacy;

namespace ScreenTail.Tests.Windows.Privacy;

/// <summary>
/// What each change of ST-042's recall round buys and costs, measured on the same readings (after
/// #275): the pages read at 1x and 2x, redacted by the rules as they were before the round and
/// with each of the rules for misreads (<see cref="MisreadRules"/>) added alone and all together, and
/// the masks of 1x and 2x together. For each: what it masks of the secrets, what it masks of pages
/// with none, and how long the engine took to read. Measurements, not floors: the floors are held on
/// the product's own reading in the tests beside these.
///
/// Run 43075790830 measured every arm, at 1.5x as well and through the viewer at every scale and
/// quality; the numbers are in docs/security/data-flow.md and PR #278. What stays here is the cheap
/// part, display scales and the 1x and 2x readings, because the Windows job has 20 minutes and reading
/// every page three times took four of them.
/// </summary>
public sealed partial class OcrScaledSecretTests
{
    /// <param name="Name">What the column is called.</param>
    /// <param name="Reads">Which readings, by index into the scales 1x and 2x, are masked together.</param>
    /// <param name="Rules">Which rules for misreads run.</param>
    private sealed record Arm(string Name, int[] Reads, MisreadRules Rules);

    private static readonly Arm[] Arms =
    [
        new("1x before", [0], MisreadRules.None),
        new("2x before", [1], MisreadRules.None),
        new("1x after", [0], MisreadRules.All),
        new("2x +prefixes", [1], MisreadRules.Prefixes),
        new("2x +random runs", [1], MisreadRules.RandomRuns),
        new("2x +bare pass", [1], MisreadRules.BarePass),
        new("2x after", [1], MisreadRules.All),
        new("1x+2x after", [0, 1], MisreadRules.All),
    ];

    /// <summary>Every arm's reading of one image, each scale read once.</summary>
    private static async Task<Reading[]> ReadArms(WindowsOcrRecogniser[] recognisers, byte[] image)
    {
        var words = new IReadOnlyList<OcrWord>[recognisers.Length];
        var ms = new double[recognisers.Length];
        for (var i = 0; i < recognisers.Length; i++)
        {
            var clock = Stopwatch.StartNew();
            words[i] = (await recognisers[i].ReadAsync(image, TestContext.Current.CancellationToken)).Words;
            ms[i] = clock.Elapsed.TotalMilliseconds;
        }

        return [.. Arms.Select(arm =>
        {
            var engine = new RedactionEngine(null, (text, policy, incomplete) => PatternLibrary.Find(text, policy, incomplete, arm.Rules));
            var redactions = arm.Reads.Select(r => engine.RedactFrame(words[r])).ToList();
            return new Reading(
                [.. arm.Reads.Select(r => words[r])],
                redactions[0] with { Regions = [.. redactions.SelectMany(r => r.Regions)], Complete = redactions.All(r => r.Complete) },
                arm.Reads.Sum(r => ms[r]));
        })];
    }

    private static WindowsOcrRecogniser[] ArmRecognisers() =>
        [new WindowsOcrRecogniser(scale: 1.0), new WindowsOcrRecogniser(scale: 2.0)];

    [Fact]
    public async Task RecallOfEachChangeIsMeasured()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var recognisers = ArmRecognisers();
        Assert.SkipUnless(recognisers[0].Available, "Windows has no OCR language pack installed.");

        var rows = new List<(string Condition, Tally[] Tallies)>();
        async Task Page(Tally[] tallies, Bitmap bitmap, List<Secret> secrets, long quality)
        {
            var readings = await ReadArms(recognisers, Encode(bitmap, quality));
            for (var arm = 0; arm < Arms.Length; arm++)
            {
                Score(readings[arm], bitmap, secrets, tallies[arm]);
            }
        }

        Tally[] New() => [.. Arms.Select(a => new Tally { SampleCap = a.Name is "1x+2x after" ? 2 : 0 })];

        foreach (var scale in new[] { 1.0, 1.25, 1.5 })
        {
            var tallies = New();
            foreach (var font in Fonts)
            {
                foreach (var points in new[] { 9f, 11f })
                {
                    for (var page = 0; page < Pages; page++)
                    {
                        using var screen = Draw(font, points, scale, page);
                        await Page(tallies, screen.Bitmap, screen.Secrets, 82);
                    }
                }
            }

            rows.Add((string.Create(CultureInfo.InvariantCulture, $"{scale * 100:0}% display"), tallies));
        }

        var table = new List<string>
        {
            "Secrets masked whole by each change, by kind (password, token, card, ssn, wrapped key), with the engine's time to read a page:",
            "| condition | secrets | " + string.Join(" | ", Arms.Select(a => a.Name)) + " |",
            "|---|---|" + string.Concat(Enumerable.Repeat("---|", Arms.Length)),
        };
        foreach (var (condition, tallies) in rows)
        {
            table.Add(string.Create(CultureInfo.InvariantCulture, $"| {condition} | {tallies[0].Secrets} | ")
                + string.Join(" | ", tallies.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Masked}: {string.Join(",", t.MaskedByKind)} ({t.OcrMs / t.Pages:0} ms)"))) + " |");
        }

        table.Add(string.Create(CultureInfo.InvariantCulture, $"| all | {rows.Sum(r => r.Tallies[0].Secrets)} | ")
            + string.Join(" | ", Enumerable.Range(0, Arms.Length).Select(a => rows.Sum(r => r.Tallies[a].Masked).ToString(CultureInfo.InvariantCulture))) + " |");
        Measurements.Record(string.Join(Environment.NewLine, table));
        Measurements.Record(StageTable("Where secrets were lost with the masks of 1x and 2x together, every rule on.", rows.Select(r => (r.Condition, r.Tallies[^1]))));

        Assert.All(rows, r => Assert.All(r.Tallies, t => Assert.True(t.Pages > 0, $"{r.Condition}: nothing was read.")));
    }

    private static readonly string[] PlainKinds = ["prose", "code", "numbers", "table"];

    /// <summary>
    /// How much of a page with no secret on it each change masks: of the ink, and of the page. Every
    /// mask here is over-masking, which is the error the rules accept; this says how much of it there is.
    /// </summary>
    [Fact]
    public async Task MaskedAreaOnPagesWithNoSecretsIsMeasured()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var recognisers = ArmRecognisers();
        Assert.SkipUnless(recognisers[0].Available, "Windows has no OCR language pack installed.");

        var table = new List<string>
        {
            "Masked on pages with no secret (Segoe UI and Consolas, 9 and 11pt, 100%, 3 pages each): share of the ink masked, share of the page masked, pages with any mask, and the finds by kind.",
            "| page | " + string.Join(" | ", Arms.Select(a => a.Name)) + " |",
            "|---|" + string.Concat(Enumerable.Repeat("---|", Arms.Length)),
        };
        var samples = new List<string>();
        foreach (var kind in PlainKinds)
        {
            var (ink, page, inkMasked, pageMasked, touched) = (new long[Arms.Length], new long[Arms.Length], new long[Arms.Length], new long[Arms.Length], new int[Arms.Length]);
            var kinds = Arms.Select(_ => new Dictionary<ScreenTail.Shared.Schema.MaskKind, int>()).ToArray();
            var examples = Arms.Select(_ => new List<string>()).ToArray();
            foreach (var font in Fonts)
            {
                foreach (var points in new[] { 9f, 11f })
                {
                    for (var n = 0; n < 3; n++)
                    {
                        using var bitmap = DrawPlain(kind, font, points, n);
                        var readings = await ReadArms(recognisers, Encode(bitmap, 82));
                        var dark = Dark(bitmap);
                        for (var arm = 0; arm < Arms.Length; arm++)
                        {
                            var masked = Masked(readings[arm].Redaction.Regions, bitmap.Width, bitmap.Height);
                            page[arm] += masked.Length;
                            ink[arm] += dark.Count(d => d);
                            var (onInk, onPage) = (0, 0);
                            for (var i = 0; i < masked.Length; i++)
                            {
                                onPage += masked[i] ? 1 : 0;
                                onInk += masked[i] && dark[i] ? 1 : 0;
                            }

                            foreach (var (found, count) in readings[arm].Redaction.Counts)
                            {
                                kinds[arm][found] = kinds[arm].GetValueOrDefault(found) + count;
                            }

                            // What was masked, as examples: the page is synthetic.
                            var under = readings[arm].Words[0].Where(w => readings[arm].Redaction.Regions.Any(r =>
                                r.X <= w.X + (w.Width / 2.0) && w.X + (w.Width / 2.0) <= r.X + r.Width && r.Y <= Middle(w) && Middle(w) <= r.Y + r.Height)).Select(w => w.Text).ToList();
                            if (under.Count > 0 && examples[arm].Count < 2 && Arms[arm].Name is "1x before" or "2x before" or "2x after")
                            {
                                examples[arm].Add(string.Join(' ', under.Take(12)));
                            }

                            inkMasked[arm] += onInk;
                            pageMasked[arm] += onPage;
                            touched[arm] += onPage > 0 ? 1 : 0;
                        }
                    }
                }
            }

            table.Add($"| {kind} | " + string.Join(" | ", Enumerable.Range(0, Arms.Length).Select(a => string.Create(
                CultureInfo.InvariantCulture,
                $"{(double)inkMasked[a] / ink[a]:0.0%} ink, {(double)pageMasked[a] / page[a]:0.0%} page, {touched[a]} of 20 ({string.Join(" ", kinds[a].OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))})"))) + " |");
            samples.AddRange(Enumerable.Range(0, Arms.Length).SelectMany(a => examples[a].Select(e => $"  {kind} {Arms[a].Name}: \"{e}\"")));
        }

        table.Add("Examples of what was masked on them (synthetic text, the words under each mask):");
        table.AddRange(samples);
        Measurements.Record(string.Join(Environment.NewLine, table));
    }

    /// <summary>
    /// How long the engine takes on a full HD frame dense with text, at each size, as the worker reads
    /// it: the median of five after one to warm up. The budget for the whole frame is 700 ms.
    /// </summary>
    [Fact]
    public async Task OcrTimeOnAFullHdFrameIsMeasured()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var recognisers = ArmRecognisers();
        Assert.SkipUnless(recognisers[0].Available, "Windows has no OCR language pack installed.");

        using var frame = new Bitmap(1920, 1080, PixelFormat.Format24bppRgb);
        frame.SetResolution(96, 96);
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(Color.White);
            for (var column = 0; column < 2; column++)
            {
                for (var block = 0; block < 4; block++)
                {
                    using var page = DrawPlain(PlainKinds[(block + column) % PlainKinds.Length], Fonts[column], 11f, block);
                    graphics.DrawImage(page, column * 960, block * 270, new Rectangle(0, 0, Math.Min(960, page.Width), Math.Min(270, page.Height)), GraphicsUnit.Pixel);
                }
            }
        }

        var image = Encode(frame, 82);
        var lines = new List<string> { string.Create(CultureInfo.InvariantCulture, $"OCR on a 1920x1080 frame dense with text (the engine reads at most {WindowsOcrRecogniser.MaxImageDimension} px a side), median of 5:") };
        foreach (var (recogniser, name) in recognisers.Zip(["1x", "2x"]))
        {
            var ct = TestContext.Current.CancellationToken;
            _ = await recogniser.ReadAsync(image, ct);
            var times = new List<double>();
            var (words, redactMs) = (0, 0.0);
            for (var i = 0; i < 5; i++)
            {
                var clock = Stopwatch.StartNew();
                var read = await recogniser.ReadAsync(image, ct);
                times.Add(clock.Elapsed.TotalMilliseconds);
                clock.Restart();
                _ = new RedactionEngine().RedactFrame(read.Words);
                redactMs = Math.Max(redactMs, clock.Elapsed.TotalMilliseconds);
                words = read.Words.Count;
            }

            lines.Add(string.Create(CultureInfo.InvariantCulture, $"- {name}: {times.Order().ElementAt(2):0} ms to read ({times.Min():0} to {times.Max():0}), {words} words, redaction at most {redactMs:0} ms"));
        }

        Measurements.Record(string.Join(Environment.NewLine, lines));
    }

    private static bool[] Masked(IEnumerable<ScreenTail.Shared.Schema.MaskedRegion> regions, int width, int height)
    {
        var masked = new bool[width * height];
        foreach (var region in regions)
        {
            var (left, right) = ((int)Math.Max(0, region.X), (int)Math.Min(width, region.X + region.Width));
            for (var y = (int)Math.Max(0, region.Y); y < Math.Min(height, region.Y + region.Height) && right > left; y++)
            {
                Array.Fill(masked, true, (y * width) + left, right - left);
            }
        }

        return masked;
    }

    /// <summary>A page with no secret on it: prose, code, a grid of numbers, or a table of devices.</summary>
    private static Bitmap DrawPlain(string kind, string family, float points, int page)
    {
        var random = new Random((page * 104729) + kind.Length + family.Length);
        string[] prose =
        [
            "the", "printer", "queue", "was", "cleared", "after", "a", "restart", "and", "customer", "reported", "that", "spooler",
            "service", "stopped", "again", "on", "Monday", "morning", "driver", "update", "fixed", "network", "share", "mapped",
            "drive", "for", "accounting", "team", "laptop", "would", "not", "connect", "to", "Wi-Fi", "until", "we", "removed",
            "old", "profile", "Outlook", "kept", "asking", "sign", "in", "mailbox", "was", "full", "archive", "policy", "applied",
        ];
        string[] names = ["user", "order", "client", "invoice", "device", "ticket", "session", "account", "report", "cache"];
        string Name() => names[random.Next(names.Length)];
        string Upper(string word) => char.ToUpperInvariant(word[0]) + word[1..];
        string Pascal() => Upper(Name()) + Upper(Name());
        Guid Id()
        {
            var bytes = new byte[16];
            random.NextBytes(bytes);
            return new Guid(bytes);
        }

        string Line() => kind switch
        {
            "prose" => string.Join(' ', Enumerable.Range(0, random.Next(8, 12)).Select(_ => prose[random.Next(prose.Length)])) + ".",
            "code" => random.Next(6) switch
            {
                0 => $"var {Name()}List = await _{Name()}Service.Get{Pascal()}Async({Name()}Id, cancellationToken);",
                1 => $"if ({Name()}.Count > {random.Next(2, 500)}) {{ return {Name()}Items[{random.Next(9)}]; }}",
                2 => $"private readonly I{Pascal()}Repository _{Name()}Repository;",
                3 => $"_logger.LogInformation(\"Processed {{Count}} {Name()}s\", {Name()}s.Count);",
                4 => random.Next(2) == 0 ? $"public const int Max{Pascal()}RetryCount = {random.Next(2, 9)};" : $"var hash = SHA256.HashData(Encoding.UTF8.GetBytes({Name()}Id)); Int32.TryParse(value, out var {Name()}Count);",
                _ => $"foreach (var item in {Name()}s.Where(x => x.IsActive && x.{Pascal()}Id != null))",
            },
            "numbers" => string.Join("   ", Enumerable.Range(0, 8).Select(_ => random.Next(3) switch
            {
                0 => random.Next(1000, 99999999).ToString(CultureInfo.InvariantCulture),
                1 => (random.Next(100, 999999) / 100.0).ToString("N2", CultureInfo.InvariantCulture),
                _ => random.Next(10, 9999).ToString(CultureInfo.InvariantCulture),
            })),
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"{(random.Next(2) == 0 ? "Reception" : "Accounts")}-PC{random.Next(1, 40):00}   10.0.{random.Next(256)}.{random.Next(256)}   2026-{random.Next(1, 13):00}-{random.Next(1, 29):00} {random.Next(24):00}:{random.Next(60):00}   {(random.Next(3) == 0 ? "Offline" : "Online")}   {Id()}"),
        };

        var pixels = (float)(points * 96.0 / 72.0);
        using var font = new Font(family, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
        var pitch = (int)Math.Ceiling(font.GetHeight(96f) * 1.15);
        const int Rows = 14;
        var bitmap = new Bitmap(BaseWidth, (2 * Margin) + (Rows * pitch), PixelFormat.Format24bppRgb);
        bitmap.SetResolution(96, 96);
        const System.Windows.Forms.TextFormatFlags Flags =
            System.Windows.Forms.TextFormatFlags.NoPadding | System.Windows.Forms.TextFormatFlags.NoPrefix
            | System.Windows.Forms.TextFormatFlags.SingleLine | System.Windows.Forms.TextFormatFlags.Left | System.Windows.Forms.TextFormatFlags.Top;
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        for (var i = 0; i < Rows; i++)
        {
            System.Windows.Forms.TextRenderer.DrawText(graphics, Line(), font, new System.Drawing.Point(Margin, Margin + (i * pitch)), Color.Black, Color.White, Flags);
        }

        return bitmap;
    }
}
