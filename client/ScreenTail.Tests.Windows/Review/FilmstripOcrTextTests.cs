using System.Runtime.Versioning;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Review;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// `T` in the enlarged view, now that the session crosses the pipe without any frame's OCR text (E-20).
///
/// The text is asked for when a frame is opened, held while it is open, and let go when it closes, the
/// same as the full image. Before, it rode inside the session for every frame at once, and a long session
/// passed the pipe's 1 MiB limit and could not be opened at all.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FilmstripOcrTextTests
{
    [Fact]
    public async Task EnlargingAFrameAsksForItsTextAndClosingLetsItGo()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new TextPerFrame(new Dictionary<string, string?> { ["f0"] = "Service status: Stopped" });
        var strip = new FilmstripViewModel(SessionWith(2), frames);
        var first = strip.Items.OfType<FrameItem>().First();
        Assert.Null(first.OcrText);
        Assert.False(first.HasOcrText);

        await strip.EnlargeCommand.ExecuteAsync(first);

        Assert.Equal("Service status: Stopped", first.OcrText);
        Assert.True(first.HasOcrText);
        Assert.Equal(["f0"], frames.Asked);

        strip.CloseEnlargedCommand.Execute(null);
        Assert.Null(first.OcrText);
        Assert.False(first.HasOcrText);
    }

    [Fact]
    public async Task EnlargingAnotherFrameLetsTheFirstOneGo()
    {
        // Enter on a second frame while the first is open moves the view without passing through Esc.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new TextPerFrame(new Dictionary<string, string?> { ["f0"] = "first", ["f1"] = "second" });
        var strip = new FilmstripViewModel(SessionWith(2), frames);
        var first = strip.Items.OfType<FrameItem>().First();
        var second = strip.Items.OfType<FrameItem>().Last();
        await strip.EnlargeCommand.ExecuteAsync(first);
        Assert.NotNull(first.Image);

        await strip.EnlargeCommand.ExecuteAsync(second);

        Assert.Same(second, strip.Enlarged);
        Assert.Equal("second", second.OcrText);
        Assert.NotNull(second.Image);
        Assert.Null(first.OcrText);
        Assert.Null(first.Image);
    }

    [Fact]
    public async Task EnlargingTheOpenFrameAgainKeepsWhatItHolds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new TextPerFrame(new Dictionary<string, string?> { ["f0"] = "first" });
        var strip = new FilmstripViewModel(SessionWith(2), frames);
        var first = strip.Items.OfType<FrameItem>().First();
        await strip.EnlargeCommand.ExecuteAsync(first);

        await strip.EnlargeCommand.ExecuteAsync(first);

        Assert.Same(first, strip.Enlarged);
        Assert.Equal("first", first.OcrText);
        Assert.NotNull(first.Image);
        Assert.Equal(["f0"], frames.Asked);
    }

    [Fact]
    public async Task DeletingTheEnlargedFrameLetsItsTextAndImageGo()
    {
        // The frame can come back with Undo, so the item outlives the delete; what it held must not.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new TextPerFrame(new Dictionary<string, string?> { ["f0"] = "first" });
        var strip = new FilmstripViewModel(SessionWith(2), frames);
        var first = strip.Items.OfType<FrameItem>().First();
        await strip.EnlargeCommand.ExecuteAsync(first);

        strip.DeleteCommand.Execute(first);

        Assert.Null(strip.Enlarged);
        Assert.Null(first.OcrText);
        Assert.Null(first.Image);
    }

    [Fact]
    public async Task AFrameWithNoTextLeavesTheToggleOff()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var strip = new FilmstripViewModel(SessionWith(2), new TextPerFrame([]));
        var second = strip.Items.OfType<FrameItem>().Last();

        await strip.EnlargeCommand.ExecuteAsync(second);

        Assert.Same(second, strip.Enlarged);
        Assert.False(second.HasOcrText);
    }

    [Fact]
    public async Task WithNoFramesSourceTheTextOnTheFrameIsShown()
    {
        // The render harness hands over a whole fixture session and no pipe; its frames carry their text.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var session = SessionWith(1);
        session = session with { Frames = [session.Frames[0] with { OcrText = "Services" }] };
        var strip = new FilmstripViewModel(session);
        var only = strip.Items.OfType<FrameItem>().Single();

        await strip.EnlargeCommand.ExecuteAsync(only);

        Assert.Equal("Services", only.OcrText);
    }

    private static Session SessionWith(int frames) => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s-ocr",
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

    /// <summary>Frames as they arrive over the pipe: no text on the frame, and the text one call away.</summary>
    private sealed class TextPerFrame(Dictionary<string, string?> text) : IReviewFrames
    {
        public List<string> Asked { get; } = [];

        public Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default) => Task.FromResult<byte[]?>([1, 2, 3]);

        public Task<string?> OcrTextAsync(Frame frame, CancellationToken ct = default)
        {
            Asked.Add(frame.Id);
            return Task.FromResult(text.GetValueOrDefault(frame.Id));
        }

        public Task SetIncludedAsync(string frameId, bool included, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(string frameId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<byte[]?> BlurAsync(string frameId, MaskedRegion region, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }
}
