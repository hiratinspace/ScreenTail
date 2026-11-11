using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using ScreenTail.Core.Privacy;
using ScreenTail.Service.Privacy;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Privacy;

/// <summary>
/// The token, row and wrap rules against the real OCR engine on text that is not at 100% (ST-042, after
/// #268 and #269): a display scaled to 125% or 150%, and a remote viewer that shows the far desktop
/// shrunk or stretched and compressed as JPEG. <see cref="OcrSplitSecretTests"/> measured the token
/// rule's cutoff on one font at 96 DPI; these ask whether what it protects holds where the frames that
/// matter come from.
///
/// Each page is a screen of known lines in a known font with five secrets on it: a password after a
/// cue, an API token, a card number, a social security number, and a key cut in two by the right edge
/// and carried on at the start of the next line. Where each secret was drawn is known to the pixel, so
/// what is asked is about the picture and not about what the engine read: of the secret's dark pixels,
/// how many are under a mask.
///
/// Three answers a secret can have. <b>Masked</b>: at least 97% of its dark pixels are covered (the
/// rest is the fringe of a glyph outside the engine's box). <b>Missed</b>: no more than 3% are, so no
/// rule fired, which is the engine misreading the secret or not reading it, a miss of recall.
/// <b>Part showing</b>: anything between. Of those, one with a piece the engine read no word on is
/// a miss of recall on that piece and is counted apart; the rest are a rule that fired and stopped
/// short, the thing the token, row and wrap rules exist to prevent.
///
/// The floors are per kind, so that a rule deleted is a test failed. Each floor is what the hosted
/// runner measured less a tenth, and less two at the least: first on 2026-11-10 (run 42930307506),
/// raised on 2026-11-11 (run 43075790830) after the frame was read at twice its size and the rules for
/// misreads were added, and never lowered, so a kind the second run measured one lower keeps the
/// first run's floor. Each bound on part showing is what was measured and two more. Reading at twice
/// the size turned many missed wrapped keys into half masked ones, the second row's piece masked and
/// the first not read, so those bounds rose with the floors; the bound on a rule that fired and
/// stopped short with every piece read is held separately. The engine reads the same picture the
/// same way on the same runner, so the margin is for a runner image that changes, not for chance.
/// Since the random-run rule, a wrapped key whose two pieces are each random enough is masked whole
/// without the wrap pass as well; the wrap pass itself is held by OcrWrapTests in the Core suite.
///
/// Counts and box geometry only are printed, never text, failing or not (INV-10).
/// </summary>
public sealed partial class OcrScaledSecretTests
{
    private const int Pages = 20;
    private const int BaseWidth = 1000;
    private const int Margin = 24;

    private static readonly string[] Fonts = ["Segoe UI", "Consolas"];
    private static readonly string[] Kinds = ["password", "token", "card", "ssn", "wrapped key"];
    private static readonly string[] FirstWords = ["Tangerine", "Marigold", "Juniper", "Lavender", "Saffron"];
    private static readonly string[] SecondWords = ["Wobble", "Quiver", "Ramble", "Tumble", "Glimmer"];

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// The same screen at 100%, 125% and 150%, drawn by Windows at that size, as a display at that
    /// scale draws it, and encoded as the capturer encodes a frame. A measurement with floors, not
    /// the proof it was written to be: the engine does not read these secrets well enough to assert
    /// none missed, at any scale, 100% included. Of 400 secrets a scale it masked 288 at 100%, 284 at
    /// 125% and 307 at 150% read as captured, and 381, 392 and 392 read at twice the size with the
    /// rules for misreads. What it still reads worst is the wrapped key (67, 74 and 73 of 80), whose
    /// first row the engine drops whole or in part ("--key BeF2" for "sk_live_Jtb1o1BeF2").
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public async Task SecretsAtEveryDisplayScaleAreMeasured(double scale)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var recogniser = new WindowsOcrRecogniser();
        Assert.SkipUnless(recogniser.Available, "Windows has no OCR language pack installed.");

        var total = new Tally();
        var stages = new List<(string, Tally)>();
        foreach (var font in Fonts)
        {
            foreach (var points in new[] { 9f, 11f })
            {
                var tally = new Tally();
                for (var page = 0; page < Pages; page++)
                {
                    using var screen = Draw(font, points, scale, page);
                    await Redact(recogniser, screen.Bitmap, screen.Secrets, 82, tally);
                }

                Measurements.Record(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Secrets through OCR at {scale * 100:0}% display scale, {font} {points:0}pt: {tally}"));
                stages.Add(($"{font} {points:0}pt", tally));
                total.Add(tally);
            }
        }

        Measurements.Record(StageTable(string.Create(CultureInfo.InvariantCulture, $"Where secrets were lost at {scale * 100:0}% display scale ({total.OcrMs / total.Pages:0} ms of OCR a page)."), stages));

        Assert.True(total.Pages == Fonts.Length * 2 * Pages, $"Drew {total.Pages} pages. {total}");
        Assert.True(total.TextLeft == 0, $"A masked secret is still in the stored text at {scale * 100:0}%. {total}");

        // Masked whole of 80 of each kind (password, token, card, ssn, wrapped key), as measured.
        var (measured, partial, stoppedShort) = scale switch
        {
            1.0 => (new[] { 80, 77, 77, 80, 67 }, 12, 0),
            1.25 => (new[] { 80, 78, 80, 80, 74 }, 6, 1),
            _ => (new[] { 80, 80, 80, 80, 73 }, 7, 5),
        };
        Hold(total, measured, partial, stoppedShort, string.Create(CultureInfo.InvariantCulture, $"{scale * 100:0}% display scale"));
    }

    /// <summary>What was measured less a tenth, and less two at the least.</summary>
    private static int Floor(int measured) => (int)Math.Floor(measured - Math.Max(2.0, measured / 10.0));

    /// <summary>
    /// Holds a tally to what the hosted runner measured: each kind masked whole no fewer times than
    /// its floor, and no more than two more part showing, counted all together and counted without
    /// those that have a piece the engine read no word on.
    /// </summary>
    private static void Hold(Tally tally, int[] masked, int partial, int stoppedShort, string where)
    {
        for (var kind = 0; kind < Kinds.Length; kind++)
        {
            Assert.True(
                tally.MaskedByKind[kind] >= Floor(masked[kind]),
                $"At {where}: {tally.MaskedByKind[kind]} of kind {Kinds[kind]} masked whole, under the floor of {Floor(masked[kind])} ({masked[kind]} measured). {tally}");
        }

        Assert.True(tally.Partial <= partial + 2, $"At {where}: {tally.Partial} secrets part showing, where {partial} were measured. {tally}");
        Assert.True(
            tally.Partial - tally.PartialUnread <= stoppedShort + 2,
            $"At {where}: a rule fired and stopped short on {tally.Partial - tally.PartialUnread} secrets with every piece read, where {stoppedShort} were measured. {tally}");
    }

    /// <summary>
    /// The 100% screen through a remote viewer: resized with bilinear filtering to each scale and
    /// encoded as JPEG at each quality, then read. This is a measurement first: it prints a table of
    /// how many secrets were masked, part showing and missed at each scale and quality. A missed secret
    /// here is the OCR engine failing on small, blurred text, which no rule of the redaction can mend,
    /// so it is counted and reported, not asserted away. What it fails on is stated at the end.
    ///
    /// What the table showed of the wrapped key at 1.1 (runs 42930307506 and 42951818496): in Segoe UI
    /// it is part showing on every page, about half covered, and on 39 of the 40 the piece left showing
    /// is the first row's, with no word read whose middle is on it; the second row's piece was read as
    /// one word on 19 of 20 and is the half that is masked. So it is not a rule that read both pieces
    /// and stopped short. How the second piece comes to be masked with the first not read is not known:
    /// none of the shapes tried in Core (first piece missing, glued to the word before it, split at its
    /// underscores, cut short) masks the one and not the other, and the probe prints no text.
    /// </summary>
    [Fact]
    public async Task SecretsThroughAScaledLossyViewerAreMeasured()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var recogniser = new WindowsOcrRecogniser();
        Assert.SkipUnless(recogniser.Available, "Windows has no OCR language pack installed.");

        double[] scales = [0.6, 0.7, 0.8, 0.9, 1.0, 1.1];
        long[] qualities = [50, 75];
        var table = new List<string>
        {
            "Secrets through a scaled, lossy viewer (11pt at 100%, bilinear resize, JPEG; 20 pages a font, 5 secrets a page):",
            "| scale | JPEG | font | secrets | masked | part showing | missed | masked by kind (password, token, card, ssn, wrapped key) | missed by kind | part showing by kind | covered share of each part showing | part showing with a piece not read, with a piece read two rows tall | wrapped keys not masked whole, by words read on the first piece (none, one, two, more); on the second | still in stored text |",
            "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|",
        };
        var results = new Dictionary<(double Scale, long Quality), Tally>();
        var stages = new List<(string, Tally)>();
        foreach (var scale in scales)
        {
            foreach (var quality in qualities)
            {
                var both = new Tally();
                foreach (var font in Fonts)
                {
                    var tally = new Tally { SampleCap = 1 };
                    for (var page = 0; page < Pages; page++)
                    {
                        using var screen = Draw(font, 11f, 1.0, page);
                        using var seen = Resize(screen.Bitmap, scale);
                        var secrets = screen.Secrets.Select(s => s with { Pieces = [.. s.Pieces.Select(p => Scale(p, scale))] }).ToList();
                        await Redact(recogniser, seen, secrets, quality, tally);
                    }

                    table.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"| {scale:0.0} | {quality} | {font} | {tally.Secrets} | {tally.Masked} | {tally.Partial} | {tally.Missed} | {string.Join(", ", tally.MaskedByKind)} | {string.Join(", ", tally.MissedByKind)} | {string.Join(", ", tally.PartialByKind)} | {tally.PartialShares} | {tally.PartialUnread}, {tally.PartialTall} | {string.Join(", ", tally.WordsOnPiece[0])}; {string.Join(", ", tally.WordsOnPiece[1])} | {tally.TextLeft} |"));
                    stages.Add((string.Create(CultureInfo.InvariantCulture, $"{scale:0.0} JPEG {quality} {font}"), tally));
                    both.Add(tally);
                }

                results[(scale, quality)] = both;
            }
        }

        Measurements.Record(string.Join(Environment.NewLine, table));
        Measurements.Record(StageTable("Where secrets were lost through the viewer.", stages));

        // What is held to. Every condition was drawn and read, nothing masked in the picture is left
        // in the stored text, and at every scale and quality each kind is masked whole no fewer times
        // than its floor and no more are part showing than were measured and two.
        Assert.All(results, r => Assert.True(r.Value.Pages == Fonts.Length * Pages, $"Scale {r.Key.Scale}, quality {r.Key.Quality}: drew {r.Value.Pages} pages."));
        Assert.All(results, r => Assert.True(r.Value.TextLeft == 0, $"Scale {r.Key.Scale}, quality {r.Key.Quality}: a secret masked in the picture is still in the stored text."));
        foreach (var (scale, quality, masked, partial, stoppedShort) in ViewerMeasured)
        {
            Hold(results[(scale, quality)], masked, partial, stoppedShort, string.Create(CultureInfo.InvariantCulture, $"viewer scale {scale:0.0}, JPEG {quality}"));
        }
    }

    /// <summary>
    /// What the hosted runner measured through the viewer, both fonts together: of 40 of each kind
    /// (password, token, card, ssn, wrapped key) how many were masked whole, how many secrets of the
    /// 200 were part showing, and how many of those had every piece read.
    /// </summary>
    private static readonly (double Scale, long Quality, int[] Masked, int Partial, int StoppedShort)[] ViewerMeasured =
    [
        (0.6, 50, [25, 5, 12, 29, 2], 24, 6),
        (0.6, 75, [34, 20, 23, 35, 4], 25, 0),
        (0.7, 50, [37, 24, 36, 38, 9], 24, 1),
        (0.7, 75, [36, 33, 39, 37, 18], 18, 2),
        (0.8, 50, [39, 32, 33, 39, 18], 22, 1),
        (0.8, 75, [40, 34, 35, 40, 27], 13, 1),
        (0.9, 50, [40, 36, 39, 40, 33], 6, 4),
        (0.9, 75, [40, 37, 40, 40, 35], 5, 2),
        (1.0, 50, [40, 39, 40, 40, 38], 2, 1),
        (1.0, 75, [40, 39, 40, 40, 32], 8, 1),
        (1.1, 50, [40, 38, 40, 39, 36], 4, 2),
        (1.1, 75, [40, 40, 40, 40, 34], 5, 4),
    ];

    private sealed partial class Tally
    {
        public int Pages { get; set; }

        public int Secrets { get; private set; }

        public int Masked { get; private set; }

        public int Partial { get; private set; }

        public int Missed { get; private set; }

        public int TextLeft { get; set; }

        public int[] MaskedByKind { get; } = new int[Kinds.Length];

        public int[] MissedByKind { get; } = new int[Kinds.Length];

        public int[] PartialByKind { get; } = new int[Kinds.Length];

        public double LeastCovered { get; private set; } = 1;

        /// <summary>
        /// Part showing secrets with a piece the engine read no word on: a miss of recall on that
        /// piece, which no rule can mend, and not a rule stopping short.
        /// </summary>
        public int PartialUnread { get; private set; }

        /// <summary>Part showing secrets with an uncovered piece under a word read two rows tall.</summary>
        public int PartialTall { get; private set; }

        /// <summary>
        /// For each two-piece secret not masked whole, how many words the engine read on its first
        /// piece and on its second: none, one, two, or more. One is the piece read as it was drawn.
        /// </summary>
        public int[][] WordsOnPiece { get; } = [new int[4], new int[4]];

        private readonly List<(int Kind, double Covered)> _partial = [];

        /// <summary>How much of each part showing secret was covered, as percentages, kind by kind.</summary>
        public string PartialShares => _partial.Count == 0
            ? "none"
            : string.Join("; ", _partial.GroupBy(p => p.Kind).OrderBy(g => g.Key).Select(g =>
                Kinds[g.Key] + " " + string.Join(" ", g.Select(p => p.Covered).Order().Select(c => c.ToString("0%", CultureInfo.InvariantCulture)))));

        public void Count(int kind, double covered, bool unread, bool tall)
        {
            Secrets++;
            if (covered >= 0.97)
            {
                Masked++;
                MaskedByKind[kind]++;
                LeastCovered = Math.Min(LeastCovered, covered);
            }
            else if (covered <= 0.03)
            {
                Missed++;
                MissedByKind[kind]++;
            }
            else
            {
                Partial++;
                PartialByKind[kind]++;
                PartialUnread += unread ? 1 : 0;
                PartialTall += tall ? 1 : 0;
                _partial.Add((kind, covered));
            }
        }

        public void Add(Tally other)
        {
            Pages += other.Pages;
            Secrets += other.Secrets;
            Masked += other.Masked;
            Partial += other.Partial;
            Missed += other.Missed;
            TextLeft += other.TextLeft;
            LeastCovered = Math.Min(LeastCovered, other.LeastCovered);
            PartialUnread += other.PartialUnread;
            PartialTall += other.PartialTall;
            _partial.AddRange(other._partial);
            AddDiagnosis(other);
            for (var i = 0; i < 4; i++)
            {
                WordsOnPiece[0][i] += other.WordsOnPiece[0][i];
                WordsOnPiece[1][i] += other.WordsOnPiece[1][i];
            }

            for (var i = 0; i < Kinds.Length; i++)
            {
                MaskedByKind[i] += other.MaskedByKind[i];
                MissedByKind[i] += other.MissedByKind[i];
                PartialByKind[i] += other.PartialByKind[i];
            }
        }

        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"{Pages} pages, {Secrets} secrets: masked **{Masked}** (by kind {string.Join(", ", MaskedByKind)}), part showing **{Partial}** (by kind {string.Join(", ", PartialByKind)}; covered {PartialShares}; with a piece not read {PartialUnread}, with a piece read two rows tall {PartialTall}), missed **{Missed}** (by kind {string.Join(", ", MissedByKind)}; kinds are {string.Join(", ", Kinds)}), "
            + $"masked in the picture and still in the stored text {TextLeft}, least covered of the masked {LeastCovered:P1}. "
            + $"Wrapped keys not masked whole, by words read on the first row's piece (none, one, two, more) {string.Join(", ", WordsOnPiece[0])} and on the second's {string.Join(", ", WordsOnPiece[1])}.");
    }

    /// <param name="Kind">Which of <see cref="Kinds"/> it is.</param>
    /// <param name="Text">What was drawn, for the stored text check.</param>
    /// <param name="Pieces">Where it was drawn: one box, or two for the key that wraps.</param>
    /// <param name="PieceTexts">What was drawn in each piece.</param>
    private sealed record Secret(int Kind, string Text, Rectangle[] Pieces, string[] PieceTexts);

    private sealed class Screen(Bitmap bitmap, List<Secret> secrets) : IDisposable
    {
        public Bitmap Bitmap { get; } = bitmap;

        public List<Secret> Secrets { get; } = secrets;

        public void Dispose() => Bitmap.Dispose();
    }

    /// <summary>
    /// Reads the picture as the worker does, redacts it, and counts for each secret how much of its
    /// ink is under a mask.
    /// </summary>
    private static Task Redact(WindowsOcrRecogniser recogniser, Bitmap bitmap, List<Secret> secrets, long quality, Tally tally) =>
        Redact(Read(recogniser), bitmap, secrets, quality, tally);

    private static async Task Redact(Reader reader, Bitmap bitmap, List<Secret> secrets, long quality, Tally tally) =>
        Score(await reader(Encode(bitmap, quality)), bitmap, secrets, tally);

    /// <summary>Counts for each secret how much of its ink is under one reading's masks.</summary>
    private static void Score(Reading reading, Bitmap bitmap, List<Secret> secrets, Tally tally)
    {
        var words = reading.Words[0];
        var redaction = reading.Redaction;
        tally.Pages++;
        tally.OcrMs += reading.OcrMs;

        var (width, height) = (bitmap.Width, bitmap.Height);
        var dark = Dark(bitmap);
        var masked = new bool[width * height];
        foreach (var region in redaction.Regions)
        {
            var (left, right) = ((int)Math.Max(0, region.X), (int)Math.Min(width, region.X + region.Width));
            for (var y = (int)Math.Max(0, region.Y); y < Math.Min(height, region.Y + region.Height) && right > left; y++)
            {
                Array.Fill(masked, true, (y * width) + left, right - left);
            }
        }

        foreach (var secret in secrets)
        {
            int ink = 0, covered = 0;
            bool unread = false, tall = false;
            foreach (var piece in secret.Pieces)
            {
                int pieceInk = 0, pieceCovered = 0;
                for (var y = Math.Max(0, piece.Top); y < Math.Min(height, piece.Bottom); y++)
                {
                    for (var x = Math.Max(0, piece.Left); x < Math.Min(width, piece.Right); x++)
                    {
                        if (dark[(y * width) + x])
                        {
                            pieceInk++;
                            pieceCovered += masked[(y * width) + x] ? 1 : 0;
                        }
                    }
                }

                ink += pieceInk;
                covered += pieceCovered;
                if (pieceCovered <= pieceInk * 0.03)
                {
                    // Words whose middle is inside the piece, and words over it that are too tall
                    // to be one row of it.
                    var on = words.Where(w => piece.IntersectsWith(new Rectangle(w.X, w.Y, w.Width, w.Height))).ToList();
                    unread |= !on.Any(w => piece.Contains(w.X + (w.Width / 2), w.Y + (w.Height / 2)));
                    tall |= on.Any(w => w.Height > piece.Height * 1.5);
                }
            }

            Assert.True(ink > 0, "A secret was drawn with no ink, so the page proves nothing.");
            var share = (double)covered / ink;
            tally.Count(secret.Kind, share, unread, tall);
            tally.Diagnose(secret, share, reading, redaction.Regions);
            if (share < 0.97 && secret.Pieces.Length == 2)
            {
                for (var i = 0; i < 2; i++)
                {
                    var read = words.Count(w => secret.Pieces[i].Contains(w.X + (w.Width / 2), w.Y + (w.Height / 2)));
                    tally.WordsOnPiece[i][Math.Min(read, 3)]++;
                }
            }

            // A secret the picture lost and the text kept: any six characters of it in a row.
            if (share >= 0.97 && Enumerable.Range(0, Math.Max(0, secret.Text.Length - 5)).Any(i => redaction.Text.Contains(secret.Text.Substring(i, 6), StringComparison.Ordinal)))
            {
                tally.TextLeft++;
            }
        }
    }

    /// <summary>
    /// One screen: ten lines of a ticket, a terminal and a form, each drawn by GDI's text renderer as a
    /// Windows Forms label or a console draws it, black on white, at <paramref name="scale"/> times
    /// the size, the way Windows draws at that display scale.
    /// </summary>
    private static Screen Draw(string family, float points, double scale, int page)
    {
        var random = new Random((page * 7919) + 269);
        string Random(string alphabet, int length) => string.Create(length, random, (span, r) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = alphabet[r.Next(alphabet.Length)];
            }
        });

        const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var password = FirstWords[page % FirstWords.Length] + random.Next(1000, 10000).ToString(CultureInfo.InvariantCulture) + SecondWords[(page / FirstWords.Length) % SecondWords.Length];
        var token = "ghp_" + Random(Letters, 36);
        var card = Card(random);
        var ssn = string.Create(CultureInfo.InvariantCulture, $"{random.Next(100, 600)}-{random.Next(10, 100)}-{random.Next(1000, 10000)}");
        var (keyStart, keyRest) = ("sk_live_" + Random(Letters, 10), Random(Letters, 16));

        // Before, the secret's pieces in order, after. A line with no secret has only the first.
        (string Before, string Secret, string After, int Kind)[] lines =
        [
            ("Front desk printer queue checklist", string.Empty, string.Empty, -1),
            ("Ticket 48213 reception printer offline since Monday morning", string.Empty, string.Empty, -1),
            ("pass: ", password, string.Empty, 0),
            ("Queue cleared and a test page sent to the front desk printer", string.Empty, string.Empty, -1),
            ("token: ", token, string.Empty, 1),
            ("Customer paid by card ", card, " and asked for a receipt by post", 2),
            ("SSN ", ssn, " on the file for the account holder", 3),
            ("deploy --target production --region eu --key ", keyStart, string.Empty, 4),
            (string.Empty, keyRest, " --dry-run", 4),
            ("Driver reinstalled and the spooler service restarted", string.Empty, string.Empty, -1),
        ];

        var pixels = (float)(points * 96.0 / 72.0 * scale);
        using var font = new Font(family, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
        var pitch = (int)Math.Ceiling(font.GetHeight(96f) * 1.15);
        var margin = (int)Math.Round(Margin * scale);
        var bitmap = new Bitmap((int)Math.Round(BaseWidth * scale), (2 * margin) + (lines.Length * pitch), PixelFormat.Format24bppRgb);
        bitmap.SetResolution(96, 96);
        var secrets = new Dictionary<int, Secret>();
        const System.Windows.Forms.TextFormatFlags Flags =
            System.Windows.Forms.TextFormatFlags.NoPadding | System.Windows.Forms.TextFormatFlags.NoPrefix
            | System.Windows.Forms.TextFormatFlags.SingleLine | System.Windows.Forms.TextFormatFlags.Left | System.Windows.Forms.TextFormatFlags.Top;
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            var limit = new Size(int.MaxValue, int.MaxValue);
            for (var i = 0; i < lines.Length; i++)
            {
                var (before, secret, after, kind) = lines[i];
                var top = margin + (i * pitch);
                System.Windows.Forms.TextRenderer.DrawText(graphics, before + secret + after, font, new System.Drawing.Point(margin, top), Color.Black, Color.White, Flags);
                if (kind < 0)
                {
                    continue;
                }

                var from = before.Length == 0 ? 0 : System.Windows.Forms.TextRenderer.MeasureText(graphics, before, font, limit, Flags).Width;
                var to = System.Windows.Forms.TextRenderer.MeasureText(graphics, before + secret, font, limit, Flags).Width;
                Assert.True(margin + to <= bitmap.Width - margin, "A line ran off the screen, so its secret was not all drawn.");

                // A pixel either way, for a glyph that leans over its cell's edge.
                var box = Rectangle.FromLTRB(margin + from - 1, top, margin + to + 1, top + pitch);
                secrets[kind] = secrets.TryGetValue(kind, out var known)
                    ? known with { Text = known.Text + secret, Pieces = [.. known.Pieces, box], PieceTexts = [.. known.PieceTexts, secret] }
                    : new Secret(kind, secret, [box], [secret]);
            }
        }

        return new Screen(bitmap, [.. secrets.Values]);
    }

    /// <summary>A sixteen digit number that passes the card checksum, in four groups of four.</summary>
    private static string Card(Random random)
    {
        var digits = new int[16];
        digits[0] = 4;
        for (var i = 1; i < 15; i++)
        {
            digits[i] = random.Next(10);
        }

        var sum = 0;
        for (var i = 0; i < 15; i++)
        {
            var value = i % 2 == 0 ? digits[i] * 2 : digits[i];
            sum += value > 9 ? value - 9 : value;
        }

        digits[15] = (10 - (sum % 10)) % 10;
        var text = string.Concat(digits.Select(d => d.ToString(CultureInfo.InvariantCulture)));
        Assert.True(PatternLibrary.PassesLuhn(text), "The test's card number does not pass the checksum.");
        return $"{text[..4]} {text[4..8]} {text[8..12]} {text[12..]}";
    }

    /// <summary>The picture a remote viewer shows: the same pixels resized with bilinear filtering.</summary>
    private static Bitmap Resize(Bitmap source, double scale)
    {
        var resized = new Bitmap((int)Math.Round(source.Width * scale), (int)Math.Round(source.Height * scale), PixelFormat.Format24bppRgb);
        resized.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        graphics.DrawImage(source, new Rectangle(0, 0, resized.Width, resized.Height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return resized;
    }

    /// <summary>A box of the 100% screen on the resized one, grown to whole pixels.</summary>
    private static Rectangle Scale(Rectangle box, double scale) => Rectangle.FromLTRB(
        (int)Math.Floor(box.Left * scale),
        (int)Math.Floor(box.Top * scale),
        (int)Math.Ceiling(box.Right * scale),
        (int)Math.Ceiling(box.Bottom * scale));

    private static byte[] Encode(Bitmap bitmap, long quality)
    {
        using var parameters = new EncoderParameters(1);
        using var parameter = new EncoderParameter(Encoder.Quality, quality);
        parameters.Param[0] = parameter;
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, JpegCodec, parameters);
        return buffer.ToArray();
    }

    /// <summary>
    /// Which pixels are ink: well darker than the paper (under 160 of 255), taken from the picture before JPEG,
    /// so the compression's ringing is not counted as text.
    /// </summary>
    private static bool[] Dark(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            var dark = new bool[bitmap.Width * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var light = (row[x * 3] + row[(x * 3) + 1] + row[(x * 3) + 2]) / 3;
                    dark[(y * bitmap.Width) + x] = light < 160;
                }
            }

            return dark;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
