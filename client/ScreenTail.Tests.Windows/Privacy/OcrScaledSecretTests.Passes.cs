using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using ScreenTail.Core.Privacy;
using ScreenTail.Service.Privacy;

namespace ScreenTail.Tests.Windows.Privacy;

/// <summary>
/// What reading a frame larger than it was captured would buy and cost (ST-042, after #275): the same
/// pages read at 1x, 1.5x and 2x, and the masks of 1x and 2x together, with what each masks of the
/// secrets, what it masks of pages with none, and how long the engine takes. Measurements, not floors.
/// </summary>
public sealed partial class OcrScaledSecretTests
{
    private static readonly string[] Passes = ["1x", "1.5x", "2x", "1x+2x"];

    [Fact]
    public async Task RecallThroughLargerReadingsIsMeasured()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var recognisers = new[] { new WindowsOcrRecogniser(), new WindowsOcrRecogniser(scale: 1.5), new WindowsOcrRecogniser(scale: 2.0) };
        Assert.SkipUnless(recognisers[0].Available, "Windows has no OCR language pack installed.");
        var readers = recognisers.Select(Read).ToArray();

        var rows = new List<(string Condition, Tally[] Tallies)>();
        async Task Page(Tally[] tallies, Bitmap bitmap, List<Secret> secrets, long quality)
        {
            var image = Encode(bitmap, quality);
            var readings = new List<Reading>();
            foreach (var reader in readers)
            {
                readings.Add(await reader(image));
            }

            readings.Add(Union(readings[0], readings[2]));
            for (var pass = 0; pass < Passes.Length; pass++)
            {
                Score(readings[pass], bitmap, secrets, tallies[pass]);
            }
        }

        Tally[] New() => [.. Passes.Select(p => new Tally { SampleCap = p is "2x" ? 2 : 0 })];

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

        foreach (var scale in new[] { 0.6, 0.7, 0.8, 0.9, 1.0, 1.1 })
        {
            foreach (var quality in new long[] { 50, 75 })
            {
                var tallies = New();
                foreach (var font in Fonts)
                {
                    for (var page = 0; page < Pages; page++)
                    {
                        using var screen = Draw(font, 11f, 1.0, page);
                        using var seen = Resize(screen.Bitmap, scale);
                        var secrets = screen.Secrets.Select(s => s with { Pieces = [.. s.Pieces.Select(p => Scale(p, scale))] }).ToList();
                        await Page(tallies, seen, secrets, quality);
                    }
                }

                rows.Add((string.Create(CultureInfo.InvariantCulture, $"viewer {scale:0.0} JPEG {quality}"), tallies));
            }
        }

        var table = new List<string>
        {
            "Secrets masked whole by each reading, by kind (password, token, card, ssn, wrapped key), with the engine's time a page:",
            "| condition | secrets | " + string.Join(" | ", Passes) + " |",
            "|---|---|" + string.Concat(Enumerable.Repeat("---|", Passes.Length)),
        };
        foreach (var (condition, tallies) in rows)
        {
            table.Add(string.Create(CultureInfo.InvariantCulture, $"| {condition} | {tallies[0].Secrets} | ")
                + string.Join(" | ", tallies.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Masked}: {string.Join(",", t.MaskedByKind)} ({t.OcrMs / t.Pages:0} ms)"))) + " |");
        }

        Measurements.Record(string.Join(Environment.NewLine, table));
        Measurements.Record(StageTable("Where secrets were lost read at 2x.", rows.Select(r => (r.Condition, r.Tallies[2]))));
        Measurements.Record(StageTable("Where secrets were lost with the masks of 1x and 2x together.", rows.Select(r => (r.Condition, r.Tallies[3]))));

        Assert.All(rows, r => Assert.All(r.Tallies, t => Assert.True(t.Pages > 0, $"{r.Condition}: nothing was read.")));
    }

    private static readonly string[] PlainKinds = ["prose", "code", "numbers", "table"];

    /// <summary>
    /// How much of a page with no secret on it each reading masks: of the ink, and of the page. Every
    /// mask here is over-masking, which is the error the rules accept; this says how much of it there is.
    /// </summary>
    [Fact]
    public async Task MaskedAreaOnPagesWithNoSecretsIsMeasured()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var recognisers = new[] { new WindowsOcrRecogniser(), new WindowsOcrRecogniser(scale: 1.5), new WindowsOcrRecogniser(scale: 2.0) };
        Assert.SkipUnless(recognisers[0].Available, "Windows has no OCR language pack installed.");
        var readers = recognisers.Select(Read).ToArray();

        var table = new List<string>
        {
            "Masked on pages with no secret (Segoe UI and Consolas, 9 and 11pt, 100%, 5 pages each): share of the ink masked, share of the page masked, pages with any mask.",
            "| page | " + string.Join(" | ", Passes) + " |",
            "|---|" + string.Concat(Enumerable.Repeat("---|", Passes.Length)),
        };
        foreach (var kind in PlainKinds)
        {
            var (ink, page, inkMasked, pageMasked, touched) = (new long[Passes.Length], new long[Passes.Length], new long[Passes.Length], new long[Passes.Length], new int[Passes.Length]);
            foreach (var font in Fonts)
            {
                foreach (var points in new[] { 9f, 11f })
                {
                    for (var n = 0; n < 5; n++)
                    {
                        using var bitmap = DrawPlain(kind, font, points, n);
                        var image = Encode(bitmap, 82);
                        var readings = new List<Reading>();
                        foreach (var reader in readers)
                        {
                            readings.Add(await reader(image));
                        }

                        readings.Add(Union(readings[0], readings[2]));
                        var dark = Dark(bitmap);
                        for (var pass = 0; pass < Passes.Length; pass++)
                        {
                            var masked = Masked(readings[pass].Redaction.Regions, bitmap.Width, bitmap.Height);
                            page[pass] += masked.Length;
                            ink[pass] += dark.Count(d => d);
                            var (onInk, onPage) = (0, 0);
                            for (var i = 0; i < masked.Length; i++)
                            {
                                onPage += masked[i] ? 1 : 0;
                                onInk += masked[i] && dark[i] ? 1 : 0;
                            }

                            inkMasked[pass] += onInk;
                            pageMasked[pass] += onPage;
                            touched[pass] += onPage > 0 ? 1 : 0;
                        }
                    }
                }
            }

            table.Add($"| {kind} | " + string.Join(" | ", Enumerable.Range(0, Passes.Length).Select(p => string.Create(
                CultureInfo.InvariantCulture,
                $"{(double)inkMasked[p] / ink[p]:0.0%} ink, {(double)pageMasked[p] / page[p]:0.0%} page, {touched[p]} of 20"))) + " |");
        }

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
        var recognisers = new[] { new WindowsOcrRecogniser(), new WindowsOcrRecogniser(scale: 1.5), new WindowsOcrRecogniser(scale: 2.0) };
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
        foreach (var (recogniser, name) in recognisers.Zip(Passes))
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
                4 => $"public const int Max{Pascal()}RetryCount = {random.Next(2, 9)};",
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
