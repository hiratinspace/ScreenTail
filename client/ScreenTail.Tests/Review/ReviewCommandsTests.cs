using System.Security.Cryptography;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Review;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Review;

/// <summary>
/// The service's side of Review over the pipe (ST-085 remainder).
///
/// The UI has no store, and until 2026-09-25 nothing on the pipe could carry a session or a frame to it:
/// the Review views were fed only by the screenshot harness, and the shell's Review area showed the word
/// "Review". These are the questions and commands that make the pane real, answered against the real
/// encrypted store so that INV-1's read-path filtering is exercised, not assumed.
/// </summary>
public sealed class ReviewCommandsTests : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "screentail-tests", $"{Guid.NewGuid():N}.db");
    private readonly RecordingMasker _masker = new();
    private SqliteSessionStore? _store;

    [Fact]
    public async Task TheSessionCrossesThePipeWithRedactedFramesOnly()
    {
        // The frame that is still pending must not be in what the UI receives. LoadSessionAsync filters
        // it; this proves the filter is on the path the pipe takes and not only on some other caller's.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: ["f1", "f2"], pending: ["f3"]);

        var reply = await commands.ReplyToAsync(new GetSessionCommand { RequestId = 1, SessionId = "s1" });

        var loaded = Assert.IsType<SessionLoaded>(reply);
        Assert.Equal(["f1", "f2"], loaded.Session.Frames.Select(f => f.Id).Order());
        Assert.All(loaded.Session.Frames, f => Assert.False(f.RedactionPending));
        Assert.NotNull(loaded.Session.Draft);
    }

    [Fact]
    public async Task ASessionThatDoesNotExistIsRefusedNotInvented()
    {
        var commands = await CommandsAsync();

        Assert.Null(await commands.ReplyToAsync(new GetSessionCommand { RequestId = 1, SessionId = "nope" }));
        var result = await commands.HandleAsync(new GetSessionCommand { RequestId = 1, SessionId = "nope" });

        Assert.NotNull(result);
        Assert.False(result.Ok);
        Assert.Contains("session", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFrameComesBackAsItsRedactedBytes()
    {
        var commands = await CommandsAsync();
        await SeedAsync(redacted: ["f1"], pending: []);

        var reply = await commands.ReplyToAsync(new GetFrameCommand { RequestId = 2, FrameId = "f1" });

        var frame = Assert.IsType<FrameLoaded>(reply);
        Assert.Equal("f1", frame.FrameId);
        Assert.Equal(RedactedBytes("f1"), frame.Image);
    }

    [Fact]
    public async Task APendingFrameIsNotAFrame()
    {
        // INV-1 on the frame path: the pending image exists in the store and must never leave it.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: ["f3"]);

        Assert.Null(await commands.ReplyToAsync(new GetFrameCommand { RequestId = 2, FrameId = "f3" }));
        var result = await commands.HandleAsync(new GetFrameCommand { RequestId = 2, FrameId = "f3" });
        Assert.False(result!.Ok);
    }

    [Fact]
    public async Task AFrameTooBigForThePipeIsRefusedNotDropped()
    {
        // Writing an oversized message closes the connection, which the UI would see as the service
        // dying. A refusal with a reason is the honest answer, and the limit is named so the number is
        // one place.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: []);
        await _store!.SaveRedactedFrameAsync("s1", Staged("huge", 5000), new RedactionOutcome(new byte[ReviewCommands.LargestFrameBytes + 1], "t", [], false, DateTimeOffset.UnixEpoch));

        Assert.Null(await commands.ReplyToAsync(new GetFrameCommand { RequestId = 3, FrameId = "huge" }));
        var result = await commands.HandleAsync(new GetFrameCommand { RequestId = 3, FrameId = "huge" });

        Assert.False(result!.Ok);
        Assert.Contains("large", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABlurIsPaintedWrittenAndHandedBack()
    {
        // In that order. The store holds the new bytes before the UI is told anything, so a crash between
        // the two cannot leave a covered password on screen over an uncovered one on disk.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: ["f1"], pending: []);

        var reply = await commands.ReplyToAsync(new BlurFrameCommand { RequestId = 4, FrameId = "f1", X = 10, Y = 20, Width = 300, Height = 40 });

        var frame = Assert.IsType<FrameLoaded>(reply);
        Assert.Equal(_masker.Painted, frame.Image);
        Assert.Equal(_masker.Painted, await _store!.GetRedactedFrameImageAsync("f1"));
        var region = Assert.Single(_masker.Regions);
        Assert.Equal((10L, 20L, 300L, 40L, MaskKind.UserBlur), (region.X, region.Y, region.Width, region.Height, region.Kind));
        var stored = (await _store.LoadSessionAsync("s1"))!.Frames.Single(f => f.Id == "f1");
        Assert.Contains(stored.MaskedRegions, r => r.Kind == MaskKind.UserBlur && r.X == 10 && r.Width == 300);
    }

    [Theory]
    // The review's case: a 1920 px capture stored at 1600. Painted unscaled, the box lands 1.2x too far
    // right and down while masked_regions says the password was covered.
    [InlineData(1920, 1080, 1600, 900, 10, 20, 300, 40)]
    [InlineData(1920, 1080, 1600, 900, 1620, 1040, 300, 40)]
    // 4K: unscaled, most of this rectangle would fall off the stored image and the paint would be clamped
    // to a sliver of the wrong corner.
    [InlineData(3840, 2160, 1600, 900, 2000, 1000, 1840, 1160)]
    // Small and off the grid, where rounding inward would leave a whole stored pixel uncovered.
    [InlineData(2560, 1600, 1600, 1000, 1, 1, 3, 3)]
    [InlineData(1080, 1920, 900, 1600, 7, 1913, 1073, 7)]
    [InlineData(5120, 1440, 1600, 450, 5119, 0, 1, 1440)]
    // Stored at its own size: nothing to scale, and nothing may move.
    [InlineData(1600, 900, 1600, 900, 10, 20, 300, 40)]
    [InlineData(800, 600, 800, 600, 0, 0, 800, 600)]
    public async Task ABlurCoversEveryStoredPixelUnderTheRectangleDrawnInTheFramesOwnPixels(
        int width, int height, int storedWidth, int storedHeight, long x, long y, long w, long h)
    {
        // T-134. The frame row records the capture's size and the stored image is downscaled, so the
        // rectangle the pane sends has to be scaled before it is painted, and rounded outward: a stored
        // pixel that shows any part of the rectangle is a pixel the technician asked to destroy.
        var masker = new SizedMasker();
        var commands = await CommandsAsync(masker);
        await SeedAsync(redacted: [], pending: []);
        await _store!.SaveRedactedFrameAsync(
            "s1",
            new StagedFrame("big", 1000, FrameTrigger.Click, width, height, null, new byte[] { 1 }),
            new RedactionOutcome(SizedMasker.Image(storedWidth, storedHeight), "text", [], false, DateTimeOffset.UnixEpoch, storedWidth, storedHeight));

        var reply = await commands.ReplyToAsync(new BlurFrameCommand { RequestId = 4, FrameId = "big", X = x, Y = y, Width = w, Height = h });

        Assert.IsType<FrameLoaded>(reply);
        var painted = Assert.Single(masker.Painted);
        AssertCoversExactly(painted.Left, painted.Right, x, w, width, storedWidth, "x");
        AssertCoversExactly(painted.Top, painted.Bottom, y, h, height, storedHeight, "y");

        // What is recorded stays in the frame's own pixels, beside every region redaction found there.
        var stored = (await _store.LoadSessionAsync("s1"))!.Frames.Single(f => f.Id == "big");
        var recorded = Assert.Single(stored.MaskedRegions);
        Assert.Equal((x, y, w, h, MaskKind.UserBlur), (recorded.X, recorded.Y, recorded.Width, recorded.Height, recorded.Kind));
    }

    [Fact]
    public async Task AFrameStoredBeforeItsSizeWasRecordedIsScaledByTheDownscaleThatMadeIt()
    {
        // Rows written before the stored size had a column. Every one was downscaled by the one rule there
        // has been, so that rule says how big its image is.
        var masker = new SizedMasker();
        var commands = await CommandsAsync(masker);
        await SeedAsync(redacted: [], pending: []);
        await _store!.SaveRedactedFrameAsync(
            "s1",
            new StagedFrame("old", 1000, FrameTrigger.Click, 1920, 1080, null, new byte[] { 1 }),
            new RedactionOutcome(SizedMasker.Image(1600, 900), "text", [], false, DateTimeOffset.UnixEpoch));

        Assert.IsType<FrameLoaded>(await commands.ReplyToAsync(new BlurFrameCommand { RequestId = 4, FrameId = "old", X = 960, Y = 540, Width = 480, Height = 270 }));

        var painted = Assert.Single(masker.Painted);
        Assert.Equal((800, 450, 1200, 675), (painted.Left, painted.Top, painted.Right, painted.Bottom));
    }

    [Fact]
    public async Task ABlurIsRefusedWhenTheImageIsNotTheSizeItsRowSays()
    {
        // The scale is only as good as the size it was worked out from. If the image the masker decoded
        // is some other size, the paint is in the wrong place, and writing it would record a covered
        // region over pixels that are still there. Nothing is written and the frame stays as it was.
        var masker = new SizedMasker();
        var commands = await CommandsAsync(masker);
        await SeedAsync(redacted: [], pending: []);
        var original = SizedMasker.Image(1280, 720);
        await _store!.SaveRedactedFrameAsync(
            "s1",
            new StagedFrame("odd", 1000, FrameTrigger.Click, 1920, 1080, null, new byte[] { 1 }),
            new RedactionOutcome(original, "text", [], false, DateTimeOffset.UnixEpoch, 1600, 900));
        var blur = new BlurFrameCommand { RequestId = 4, FrameId = "odd", X = 10, Y = 20, Width = 300, Height = 40 };

        Assert.Null(await commands.ReplyToAsync(blur));
        var result = await commands.HandleAsync(blur);

        Assert.False(result!.Ok);
        Assert.Equal(original, await _store.GetRedactedFrameImageAsync("odd"));
        var frame = (await _store.LoadSessionAsync("s1"))!.Frames.Single(f => f.Id == "odd");
        Assert.Empty(frame.MaskedRegions);
        Assert.Equal("text", frame.OcrText);
    }

    [Theory]
    [InlineData(-1, 0, 10, 10)]
    [InlineData(0, -5, 10, 10)]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, -3)]
    [InlineData(1915, 0, 10, 10)]
    [InlineData(0, 1075, 10, 10)]
    [InlineData(1920, 0, 1, 1)]
    [InlineData(long.MaxValue, 0, 1, 1)]
    [InlineData(0, 0, long.MaxValue, 1)]
    [InlineData(1, 1, long.MaxValue, long.MaxValue)]
    public async Task ARectangleThatIsNotInsideTheFrameIsRefusedAndNothingIsPainted(long x, long y, long w, long h)
    {
        // The pane always sends a rectangle inside the frame (BlurRegion clamps it). Anything else is not
        // a blur this can honour: painting a clamped part of it would record a region it did not cover.
        var masker = new SizedMasker();
        var commands = await CommandsAsync(masker);
        await SeedAsync(redacted: [], pending: []);
        var original = SizedMasker.Image(1600, 900);
        await _store!.SaveRedactedFrameAsync(
            "s1",
            new StagedFrame("f1", 1000, FrameTrigger.Click, 1920, 1080, null, new byte[] { 1 }),
            new RedactionOutcome(original, "text", [], false, DateTimeOffset.UnixEpoch, 1600, 900));
        var blur = new BlurFrameCommand { RequestId = 4, FrameId = "f1", X = x, Y = y, Width = w, Height = h };

        Assert.Null(await commands.ReplyToAsync(blur));
        var result = await commands.HandleAsync(blur);

        Assert.False(result!.Ok);
        Assert.Contains("inside", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(masker.Painted);
        Assert.Equal(original, await _store.GetRedactedFrameImageAsync("f1"));
    }

    [Fact]
    public async Task TwoBlursOnOneFrameAtOnceBothSurvive()
    {
        // Each blur reads the image, paints it and writes it back. Two interleaved would both start from
        // the same picture, and the later write would wipe out the earlier paint while masked_regions
        // kept both: a region recorded as covered over pixels that are on disk again.
        var masker = new SizedMasker { HoldFirst = true };
        var commands = await CommandsAsync(masker);
        await SeedAsync(redacted: [], pending: []);
        await _store!.SaveRedactedFrameAsync(
            "s1",
            new StagedFrame("f1", 1000, FrameTrigger.Click, 1600, 900, null, new byte[] { 1 }),
            new RedactionOutcome(SizedMasker.Image(1600, 900), "text", [], false, DateTimeOffset.UnixEpoch, 1600, 900));

        var first = Task.Run(() => commands.ReplyToAsync(new BlurFrameCommand { RequestId = 4, FrameId = "f1", X = 10, Y = 10, Width = 50, Height = 50 }), TestContext.Current.CancellationToken);
        Assert.True(masker.FirstEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the first blur never reached the masker");
        var second = Task.Run(() => commands.ReplyToAsync(new BlurFrameCommand { RequestId = 5, FrameId = "f1", X = 500, Y = 500, Width = 50, Height = 50 }), TestContext.Current.CancellationToken);

        // Long enough for an unguarded second blur to read the unpainted image and reach the masker.
        _ = masker.SecondEntered.Wait(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        masker.ReleaseFirst.Set();
        Assert.IsType<FrameLoaded>(await first);
        Assert.IsType<FrameLoaded>(await second);

        Assert.Equal(2, masker.Inputs.Count);
        Assert.Equal(masker.Outputs[0], masker.Inputs[1]);
        Assert.Equal(masker.Outputs[1], await _store.GetRedactedFrameImageAsync("f1"));
        var frame = (await _store.LoadSessionAsync("s1"))!.Frames.Single(f => f.Id == "f1");
        Assert.Equal(2, frame.MaskedRegions.Count(r => r.Kind == MaskKind.UserBlur));
    }

    [Fact]
    public async Task ABlurTakesTheTextReadFromTheFrameWithIt()
    {
        // S-18. The text was read from the pixels the technician has just destroyed, and nothing records
        // which words sat under the rectangle. Kept, it is shown by the OCR toggle and sent with the next
        // draft, which is the thing the blur was for.
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: []);
        await _store!.SaveRedactedFrameAsync(
            "s1",
            new StagedFrame("f1", 1000, FrameTrigger.Click, 1600, 900, null, new byte[] { 1 }),
            new RedactionOutcome(RedactedBytes("f1"), "Password: hunter2", [], false, DateTimeOffset.UnixEpoch));

        Assert.IsType<FrameLoaded>(await commands.ReplyToAsync(new BlurFrameCommand { RequestId = 4, FrameId = "f1", X = 10, Y = 20, Width = 300, Height = 40 }));

        var frame = (await _store.LoadSessionAsync("s1"))!.Frames.Single(f => f.Id == "f1");
        Assert.Null(frame.OcrText);
    }

    [Fact]
    public async Task ABlurOnAFrameThatIsGoneIsRefused()
    {
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: []);

        Assert.Null(await commands.ReplyToAsync(new BlurFrameCommand { RequestId = 4, FrameId = "gone", X = 0, Y = 0, Width = 10, Height = 10 }));
        var result = await commands.HandleAsync(new BlurFrameCommand { RequestId = 4, FrameId = "gone", X = 0, Y = 0, Width = 10, Height = 10 });

        Assert.False(result!.Ok);
        Assert.Empty(_masker.Regions);
    }

    [Fact]
    public async Task ExcludingAndDeletingPersist()
    {
        var commands = await CommandsAsync();
        await SeedAsync(redacted: ["f1", "f2"], pending: []);

        var excluded = await commands.HandleAsync(new SetFrameIncludedCommand { RequestId = 5, FrameId = "f1", Included = false });
        var deleted = await commands.HandleAsync(new DeleteFrameCommand { RequestId = 6, FrameId = "f2" });
        var again = await commands.HandleAsync(new DeleteFrameCommand { RequestId = 7, FrameId = "f2" });

        Assert.True(excluded!.Ok);
        Assert.True(deleted!.Ok);
        Assert.False(again!.Ok);
        var session = (await _store!.LoadSessionAsync("s1"))!;
        Assert.True(session.Frames.Single(f => f.Id == "f1").ExcludedByUser);
        Assert.DoesNotContain(session.Frames, f => f.Id == "f2");
    }

    [Fact]
    public async Task TheDraftWrittenIsTheDraftReadBack()
    {
        var commands = await CommandsAsync();
        await SeedAsync(redacted: [], pending: []);
        var edited = Draft() with { Problem = "Nothing printed from reception." };

        var saved = await commands.HandleAsync(new SaveDraftCommand { RequestId = 8, SessionId = "s1", Draft = edited });
        var reply = await commands.ReplyToAsync(new GetSessionCommand { RequestId = 9, SessionId = "s1" });

        Assert.True(saved!.Ok);
        Assert.Equal("Nothing printed from reception.", Assert.IsType<SessionLoaded>(reply).Session.Draft!.Problem);
    }

    [Fact]
    public async Task CommandsItDoesNotOwnAreLeftForTheController()
    {
        // Null, not a failure: the controller chains this before its own switch, and a "not mine" that
        // read as "refused" would turn every start command into a refusal.
        var commands = await CommandsAsync();

        Assert.Null(await commands.ReplyToAsync(new StartCommand { RequestId = 1 }));
        Assert.Null(await commands.HandleAsync(new StartCommand { RequestId = 1 }));
    }

    private async Task<ReviewCommands> CommandsAsync(IFrameMasker? masker = null)
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)));
        return new ReviewCommands(_store, masker ?? _masker);
    }

    /// <summary>
    /// Asserts that the painted span on one axis is exactly the stored pixels that show any part of the
    /// drawn span: every one of them, so nothing under the rectangle survives, and no others, so the
    /// rounding is outward by less than a pixel rather than by a margin nobody asked for.
    /// </summary>
    private static void AssertCoversExactly(long paintedFrom, long paintedTo, long drawnFrom, long drawnLength, int native, int stored, string axis)
    {
        for (long pixel = 0; pixel < stored; pixel++)
        {
            // Stored pixel p shows the frame's pixels [p * native / stored, (p + 1) * native / stored), in
            // exact integers: does that meet [drawnFrom, drawnFrom + drawnLength)?
            var shows = pixel * native < (drawnFrom + drawnLength) * stored && (pixel + 1) * native > drawnFrom * stored;
            var painted = pixel >= paintedFrom && pixel < paintedTo;
            Assert.True(shows == painted, $"{axis} = {pixel}: {(shows ? "under the rectangle but left unpainted" : "painted though nothing of the rectangle is there")} (painted [{paintedFrom}, {paintedTo}))");
        }
    }

    private async Task SeedAsync(string[] redacted, string[] pending)
    {
        var store = _store!;
        await store.CreateSessionAsync(new NewSession("s1", DateTimeOffset.UnixEpoch, new RemoteTool { Kind = RemoteToolKind.Rdp }, false, null));
        foreach (var id in redacted)
        {
            await store.SaveRedactedFrameAsync("s1", Staged(id, 1000), new RedactionOutcome(RedactedBytes(id), "Service status: Stopped", [], false, DateTimeOffset.UnixEpoch));
        }

        foreach (var id in pending)
        {
            await store.StageFrameAsync("s1", Staged(id, 2000));
        }

        await store.SaveDraftAsync("s1", Draft());
        await store.FinalizeSessionAsync("s1", new FinalizeInfo(10_000, false));
    }

    private static StagedFrame Staged(string id, long ts) => new(id, ts, FrameTrigger.Click, 1600, 900, null, new byte[] { 1, 2, 3 });

    private static byte[] RedactedBytes(string id) => [.. System.Text.Encoding.ASCII.GetBytes("redacted-" + id)];

    /// <summary>Paints nothing; remembers what it was asked to paint and hands back bytes of its own.</summary>
    private sealed class RecordingMasker : IFrameMasker
    {
        public byte[] Painted { get; } = [0xCC, 0xCC, 0xCC];

        public List<MaskedRegion> Regions { get; } = [];

        public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge)
        {
            Regions.AddRange(regions);
            return new MaskedImage(Painted, 1600, 900);
        }
    }

    /// <summary>
    /// A masker that knows how big the image it was given is, as the real one does once it has decoded
    /// it, and paints what it is asked to the way the real one does: clamped to that image. The image is
    /// eight bytes of width and height, then a byte telling one output from another.
    /// </summary>
    private sealed class SizedMasker : IFrameMasker
    {
        private readonly Lock _lock = new();

        public bool HoldFirst { get; init; }

        public ManualResetEventSlim FirstEntered { get; } = new();

        public ManualResetEventSlim SecondEntered { get; } = new();

        public ManualResetEventSlim ReleaseFirst { get; } = new();

        /// <summary>Every rectangle painted, after clamping, as [left, right) by [top, bottom).</summary>
        public List<(long Left, long Top, long Right, long Bottom)> Painted { get; } = [];

        public List<byte[]> Inputs { get; } = [];

        public List<byte[]> Outputs { get; } = [];

        public static byte[] Image(int width, int height, byte marker = 0)
        {
            var bytes = new byte[9];
            BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), width);
            BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), height);
            bytes[8] = marker;
            return bytes;
        }

        public MaskedImage Mask(ReadOnlyMemory<byte> image, IReadOnlyList<MaskedRegion> regions, int maxEdge)
        {
            var input = image.ToArray();
            var width = BitConverter.ToInt32(input, 0);
            var height = BitConverter.ToInt32(input, 4);
            int call;
            lock (_lock)
            {
                Inputs.Add(input);
                call = Inputs.Count;
            }

            if (call == 1 && HoldFirst)
            {
                FirstEntered.Set();
                ReleaseFirst.Wait(TimeSpan.FromSeconds(10));
            }
            else if (call == 2)
            {
                SecondEntered.Set();
            }

            foreach (var region in regions)
            {
                var left = Math.Clamp(region.X, 0, width);
                var top = Math.Clamp(region.Y, 0, height);
                var right = Math.Clamp(region.X + region.Width, left, width);
                var bottom = Math.Clamp(region.Y + region.Height, top, height);
                lock (_lock)
                {
                    Painted.Add((left, top, right, bottom));
                }
            }

            var output = Image(width, height, (byte)(0xC0 + call));
            lock (_lock)
            {
                Outputs.Add(output);
            }

            return new MaskedImage(output, width, height);
        }
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
