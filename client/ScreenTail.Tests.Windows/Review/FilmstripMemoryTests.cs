using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Review;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// What the filmstrip holds in memory for a long session (ST-049, weaknesses P2-3).
///
/// Bound as bytes, WPF decoded every screenshot at native size to draw a 160×90 thumbnail: 5.8 MB of
/// pixels a frame, 860 MB for 150 frames, and an out-of-memory before the technician had scrolled. The
/// ticket's number is 150 frames under 150 MB. This measures the process rather than the heap, because
/// decoded bitmaps live in native memory the collector does not count.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FilmstripMemoryTests
{
    private const int Frames = 150;
    private const long Budget = 150L * 1024 * 1024;

    [Fact]
    public async Task AHundredAndFiftyFramesStayUnderTheBudget()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var image = BusyScreenshot();
        var session = SessionWith(Frames);
        var frames = new SameImageForEveryFrame(image);
        var strip = new FilmstripViewModel(session, frames);

        var before = WorkingSet();
        await strip.LoadImagesAsync(TestContext.Current.CancellationToken);
        var after = WorkingSet();

        var items = strip.Items.OfType<FrameItem>().ToList();
        Assert.Equal(Frames, items.Count);
        Assert.All(items, item => Assert.NotNull(item.Thumbnail));
        Assert.All(items, item => Assert.Null(item.Image));
        var growth = after - before;
        Measurements.Record($"filmstrip: {Frames} frames of {image.Length / 1024} KB grew the working set by {growth / (1024 * 1024)} MB (budget {Budget / (1024 * 1024)} MB)");
        Assert.True(growth < Budget, $"{Frames} thumbnails grew the working set by {growth / (1024 * 1024)} MB; the budget is {Budget / (1024 * 1024)} MB");
    }

    [Fact]
    public async Task EnlargingFetchesTheFullImageAndClosingLetsItGo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var image = BusyScreenshot();
        var strip = new FilmstripViewModel(SessionWith(2), new SameImageForEveryFrame(image));
        await strip.LoadImagesAsync(TestContext.Current.CancellationToken);
        var first = strip.Items.OfType<FrameItem>().First();

        await strip.EnlargeCommand.ExecuteAsync(first);
        Assert.Same(first, strip.Enlarged);
        Assert.Equal(image, first.Image);

        strip.CloseEnlargedCommand.Execute(null);
        Assert.Null(first.Image);
        Assert.NotNull(first.Thumbnail);
    }

    private static long WorkingSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        return self.WorkingSet64;
    }

    /// <summary>A 1600×900 JPEG with enough noise in it to be the size a real redacted frame is.</summary>
    private static byte[] BusyScreenshot()
    {
        using var bitmap = new Bitmap(1600, 900, PixelFormat.Format24bppRgb);
        var random = new Random(7);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            for (var i = 0; i < 4000; i++)
            {
                using var brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
                graphics.FillRectangle(brush, random.Next(1600), random.Next(900), random.Next(4, 40), random.Next(4, 40));
            }
        }

        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Jpeg);
        return buffer.ToArray();
    }

    private static Session SessionWith(int frames) => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s-memory",
        StartedAt = DateTimeOffset.UnixEpoch,
        RemoteTool = new RemoteTool { Kind = RemoteToolKind.Rdp },
        PartialCapture = false,
        FramesPurgedUnredacted = 0,
        LocalOnly = false,
        Events = [],
        Transcript = [],
        Draft = null,
        Frames = [.. Enumerable.Range(0, frames).Select(i => new Frame
        {
            Id = $"f{i}",
            TsMs = i * 1000L,
            Trigger = FrameTrigger.Click,
            Image = $"frames/f{i}.jpg",
            Width = 1600,
            Height = 900,
            RedactionPending = false,
            RedactedAt = DateTimeOffset.UnixEpoch,
            MaskedRegions = [],
            SensitiveContext = false,
            ExcludedByUser = false,
        })],
    };

    private sealed class SameImageForEveryFrame(byte[] image) : IReviewFrames
    {
        public Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default) => Task.FromResult<byte[]?>(image);

        public Task SetIncludedAsync(string frameId, bool included, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(string frameId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<byte[]?> BlurAsync(string frameId, MaskedRegion region, CancellationToken ct = default) => Task.FromResult<byte[]?>(image);
    }
}
