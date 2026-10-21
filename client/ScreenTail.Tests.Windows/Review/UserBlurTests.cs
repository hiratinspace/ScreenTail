using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using ScreenTail.Core.Review;
using ScreenTail.Core.Store;
using ScreenTail.Service.Privacy;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Review;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// A Review blur against the real masker and the real filmstrip (ST-075, T-134, S-18).
///
/// The Mac tests prove the arithmetic with a masker that only pretends to paint. This one paints: a
/// 1080p capture stored at 1600 px, a rectangle drawn in the capture's pixels, and the stored JPEG
/// decoded afterwards to see which pixels went black. Before the scale, the box landed 1.2x too far
/// right and down, and masked_regions said the right place had been covered.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UserBlurTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;

    [Fact]
    public async Task ABlurOnADownscaledFrameBlackensTheStoredPixelsUnderWhatWasDrawn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var ct = TestContext.Current.CancellationToken;
        var masker = new WindowsFrameMasker();

        // What redaction does to a 1920x1080 capture: nothing to cover, shrunk to 1600x900 for storage.
        var stored = masker.Mask(Plain(1920, 1080), [], maxEdge: 1600);
        Assert.Equal((1600, 900), (stored.Width, stored.Height));

        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)), ct: ct);
        await _store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null), ct);
        await _store.SaveRedactedFrameAsync(
            "s1",
            new StagedFrame("f1", 1000, FrameTrigger.Click, 1920, 1080, null, new byte[] { 1 }),
            new RedactionOutcome(stored.Image, "Password: hunter2", [], false, DateTimeOffset.UnixEpoch, stored.Width, stored.Height),
            ct);
        var commands = new ReviewCommands(_store, masker);

        // The bottom-right quarter of the capture, in the capture's pixels: 960..1440 by 540..810.
        var reply = await commands.ReplyToAsync(new BlurFrameCommand { RequestId = 1, FrameId = "f1", X = 960, Y = 540, Width = 480, Height = 270 }, ct);

        var blurred = Assert.IsType<FrameLoaded>(reply).Image;
        Assert.Equal(blurred, await _store.GetRedactedFrameImageAsync("f1", ct));
        using var image = new Bitmap(new MemoryStream(blurred));
        Assert.Equal((1600, 900), (image.Width, image.Height));

        // In the stored image that rectangle is 800..1200 by 450..675. Sampled two pixels in from each
        // edge, because JPEG blurs a hard edge by a pixel or so either way.
        for (var x = 802; x < 1198; x += 6)
        {
            for (var y = 452; y < 673; y += 6)
            {
                Assert.True(IsBlack(image.GetPixel(x, y)), $"({x}, {y}) is under the rectangle and was left showing: {image.GetPixel(x, y)}");
            }
        }

        // Where the unscaled rectangle would have gone, and the rest of the frame, is untouched. A blur
        // that over-paints is not a leak, but one painted in the wrong place is the defect this pins.
        Assert.False(IsBlack(image.GetPixel(1300, 750)), "the blur was painted in the capture's pixels, not the stored image's");
        Assert.False(IsBlack(image.GetPixel(790, 440)), "the blur spread above and left of what was drawn");
        Assert.False(IsBlack(image.GetPixel(1210, 685)), "the blur spread below and right of what was drawn");

        // And the text read from what was just destroyed is gone with it (S-18).
        var frame = Assert.Single((await _store.LoadSessionAsync("s1", ct))!.Frames);
        Assert.Null(frame.OcrText);
        var region = Assert.Single(frame.MaskedRegions);
        Assert.Equal((960L, 540L, 480L, 270L), (region.X, region.Y, region.Width, region.Height));
    }

    [Fact]
    public async Task ABlurredFrameStopsOfferingItsText()
    {
        // S-18 in the pane. The enlarged view's OCR panel is bound to the item's text; the blur has
        // withdrawn it, so the panel closes and the toggle has nothing to show.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var picture = Plain(160, 90);
        var strip = new FilmstripViewModel(SessionWith("Password: hunter2"), new BlurringFrames(picture));
        await strip.LoadImagesAsync(TestContext.Current.CancellationToken);
        var item = strip.Items.OfType<FrameItem>().Single();
        await strip.EnlargeCommand.ExecuteAsync(item);
        strip.ToggleOcrTextCommand.Execute(null);
        Assert.True(strip.ShowOcrText);
        Assert.True(item.HasOcrText);
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await strip.BlurAsync(item, new Core.Review.Rect(10, 10, 60, 20), new Core.Review.Size(160, 90), TestContext.Current.CancellationToken);

        Assert.Null(item.OcrText);
        Assert.False(item.HasOcrText);
        Assert.False(strip.ShowOcrText);
        Assert.Contains(nameof(FrameItem.OcrText), changed);
        Assert.Contains(nameof(FrameItem.HasOcrText), changed);
    }

    private static bool IsBlack(Color pixel) => pixel.R < 40 && pixel.G < 40 && pixel.B < 40;

    /// <summary>A light grey JPEG, so black stands out wherever the masker painted.</summary>
    private static byte[] Plain(int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(243, 243, 243));
        }

        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Jpeg);
        return buffer.ToArray();
    }

    private static Session SessionWith(string ocrText) => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s-blur",
        StartedAt = DateTimeOffset.UnixEpoch,
        RemoteTool = new RemoteTool { Kind = RemoteToolKind.Rdp },
        PartialCapture = false,
        FramesPurgedUnredacted = 0,
        LocalOnly = false,
        Events = [],
        Transcript = [],
        Draft = null,
        Frames =
        [
            new Frame
            {
                Id = "f1",
                TsMs = 1000,
                Trigger = FrameTrigger.Click,
                Image = "frames/f1.jpg",
                Width = 1600,
                Height = 900,
                RedactionPending = false,
                RedactedAt = DateTimeOffset.UnixEpoch,
                OcrText = ocrText,
                MaskedRegions = [],
                SensitiveContext = false,
                ExcludedByUser = false,
            },
        ],
    };

    private sealed class BlurringFrames(byte[] image) : IReviewFrames
    {
        public Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default) => Task.FromResult<byte[]?>(image);

        public Task<bool> SetIncludedAsync(string frameId, bool included, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> DeleteAsync(string frameId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<byte[]?> BlurAsync(string frameId, MaskedRegion region, CancellationToken ct = default) => Task.FromResult<byte[]?>(image);
    }

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            File.Delete(_path + suffix);
        }
    }
}
