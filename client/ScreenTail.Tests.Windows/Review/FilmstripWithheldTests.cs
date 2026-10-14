using System.Runtime.Versioning;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;
using ScreenTail.UI.Review;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// A frame that looked sensitive is shown, says it is withheld, and never reaches the publish list
/// (ST-078). The rule itself is Core's and tested there; this is the binding.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FilmstripWithheldTests
{
    [Fact]
    public async Task ASensitiveFrameSaysWithheldAndSpaceLeavesItOut()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames();
        var strip = new FilmstripViewModel(SessionWith(Frame("f1", 1000, sensitive: false), Frame("f2", 2000, sensitive: true)), frames);
        var sensitive = strip.Items.OfType<FrameItem>().Single(item => item.Id == "f2");

        await strip.ToggleIncludeCommand.ExecuteAsync(sensitive);

        Assert.Equal("withheld", sensitive.StateLabel);
        Assert.False(sensitive.Included);
        Assert.Equal(["f1"], strip.IncludedFrameIds);
        Assert.Equal("1 of 2 included", strip.Header);
        Assert.Empty(frames.Included);
    }

    [Fact]
    public async Task AnOrdinaryFrameStillSaysExcludedAndIsPersisted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var frames = new RecordingFrames();
        var strip = new FilmstripViewModel(SessionWith(Frame("f1", 1000, sensitive: false)), frames);
        var item = strip.Items.OfType<FrameItem>().Single();

        await strip.ToggleIncludeCommand.ExecuteAsync(item);

        Assert.Equal("excluded", item.StateLabel);
        Assert.Equal([("f1", false)], frames.Included);
    }

    private static Session SessionWith(params Frame[] frames) => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s-withheld",
        StartedAt = DateTimeOffset.UnixEpoch,
        RemoteTool = new RemoteTool { Kind = RemoteToolKind.Rdp },
        PartialCapture = false,
        FramesPurgedUnredacted = 0,
        LocalOnly = false,
        Events = [],
        Transcript = [],
        Draft = null,
        Frames = frames,
    };

    private static Frame Frame(string id, long tsMs, bool sensitive) => new()
    {
        Id = id,
        TsMs = tsMs,
        Trigger = FrameTrigger.Click,
        Image = $"frames/{id}.jpg",
        Width = 1600,
        Height = 900,
        RedactionPending = false,
        RedactedAt = DateTimeOffset.UnixEpoch,
        MaskedRegions = [],
        SensitiveContext = sensitive,
        ExcludedByUser = false,
    };

    private sealed class RecordingFrames : IReviewFrames
    {
        public List<(string Id, bool Included)> Included { get; } = [];

        public Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

        public Task SetIncludedAsync(string frameId, bool included, CancellationToken ct = default)
        {
            Included.Add((frameId, included));
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string frameId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<byte[]?> BlurAsync(string frameId, MaskedRegion region, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    }
}
