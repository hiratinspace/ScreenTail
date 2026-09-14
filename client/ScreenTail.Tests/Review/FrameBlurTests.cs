using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Review;

/// <summary>ST-075: a blur is destructive, so the order of the steps is the whole of the guarantee.</summary>
public sealed class FrameBlurTests
{
    private static readonly Size Displayed = new(800, 450);
    private static readonly Rect Drawn = Rect.Between(100, 50, 260, 98);

    private readonly RecordingFrames _frames = new();
    private readonly List<MaskedRegion> _flattened = [];

    [Fact]
    public async Task TheRectangleFlattenedIsTheRectangleRecorded()
    {
        // If these could differ, masked_regions would be a false statement about a picture nobody can
        // check any more - the record says the password was covered while the pixels that were destroyed
        // are somewhere else on the frame.
        var result = await Blur().ApplyAsync(Frame(), [0xAA], Drawn, Displayed);

        Assert.NotNull(result);
        var flattenedIn = Assert.Single(_flattened);
        Assert.Same(flattenedIn, _frames.LastRegion);
        Assert.Same(flattenedIn, result.Region);
    }

    [Fact]
    public async Task TheStoreHasTheBlurredBytesBeforeTheCallerIsToldAnything()
    {
        // The natural ordering is the dangerous one: update what the technician sees, then persist. That
        // leaves a window where the pane shows a covered password and the disk holds an uncovered one, and
        // a crash inside it keeps the very thing the technician asked to destroy. Nothing is returned
        // until the write has happened.
        var writes = new TaskCompletionSource();
        var frames = new RecordingFrames { Gate = writes.Task };
        var blur = new FrameBlur(frames, Flatten);

        var applying = blur.ApplyAsync(Frame(), [0xAA], Drawn, Displayed);

        Assert.False(applying.IsCompleted, "the caller was given a result before the store was written");
        writes.SetResult();
        var result = await applying;

        Assert.NotNull(result);
        Assert.Equal(result.Image, frames.LastImage);
    }

    [Fact]
    public async Task TheReturnedFrameKeepsWhatRedactionAlreadyFound()
    {
        // The technician's region is appended. Replacing the list would erase the record that the
        // redaction worker masked a card number, which is the evidence INV-1 ever ran.
        var already = new MaskedRegion { X = 1, Y = 2, Width = 30, Height = 10, Kind = MaskKind.Card };

        var result = await Blur().ApplyAsync(Frame() with { MaskedRegions = [already] }, [0xAA], Drawn, Displayed);

        Assert.Equal([MaskKind.Card, MaskKind.UserBlur], result!.Frame.MaskedRegions.Select(region => region.Kind));
    }

    [Fact]
    public async Task AFrameWhoseImageNeverLoadedIsNotTouched()
    {
        // There is nothing to flatten and no way to check what was flattened. Writing anyway would replace
        // a real frame with a rectangle drawn over nothing.
        Assert.Null(await Blur().ApplyAsync(Frame(), image: null, Drawn, Displayed));

        Assert.Empty(_flattened);
        Assert.Null(_frames.LastRegion);
    }

    [Fact]
    public async Task AClickThatWobbledDestroysNothing()
    {
        // Otherwise clicking an enlarged frame permanently destroys four pixels of it, with no undo the
        // technician knew they needed.
        Assert.Null(await Blur().ApplyAsync(Frame(), [0xAA], new Rect(10, 10, 2, 2), Displayed));

        Assert.Empty(_flattened);
        Assert.Null(_frames.LastRegion);
    }

    private FrameBlur Blur() => new(_frames, Flatten);

    private byte[] Flatten(byte[] image, MaskedRegion region)
    {
        _flattened.Add(region);
        return [0xCC, (byte)_flattened.Count];
    }

    private static Frame Frame() => new()
    {
        Id = "f1",
        TsMs = 1000,
        Trigger = FrameTrigger.Click,
        Image = "f1.png",
        Width = 1600,
        Height = 900,
        RedactionPending = false,
        RedactedAt = DateTimeOffset.UnixEpoch,
        MaskedRegions = [],
        SensitiveContext = false,
        ExcludedByUser = false,
    };

    private sealed class RecordingFrames : IReviewFrames
    {
        public Task? Gate { get; init; }

        public MaskedRegion? LastRegion { get; private set; }

        public byte[]? LastImage { get; private set; }

        public Task<byte[]?> ImageAsync(Frame frame, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetIncludedAsync(string frameId, bool included, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string frameId, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task BlurAsync(string frameId, ReadOnlyMemory<byte> image, MaskedRegion region, CancellationToken ct = default)
        {
            if (Gate is { } gate)
            {
                await gate.ConfigureAwait(false);
            }

            LastRegion = region;
            LastImage = image.ToArray();
        }
    }
}
