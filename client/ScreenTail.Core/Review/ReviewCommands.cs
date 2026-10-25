using System.Runtime.CompilerServices;
using ScreenTail.Core.Privacy;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Review;

/// <summary>
/// The service's side of Review over the pipe (ST-085 remainder).
///
/// The UI has no store, and the one process that reads a frame is this one. So every question and
/// command the Review pane has is answered here: the session as the store hands it out (redacted frames
/// only, INV-1) less its OCR text, one frame's bytes or text at a time, and the edits (include, delete,
/// blur, save) written before the UI is told anything.
///
/// Two entry points, matching <see cref="Ipc.IIpcCommandHandler"/>: a question is answered with its own
/// event by <see cref="ReplyToAsync"/>, and when there is no answer <see cref="HandleAsync"/> says why.
/// Both return null for a command that is not theirs, so the controller can chain this ahead of its own
/// switch without a "not mine" ever reading as "refused".
/// </summary>
#pragma warning disable CA1001 // _blurring never hands out its wait handle, so there is nothing for a Dispose to release.
public sealed class ReviewCommands(ISessionStore store, IFrameMasker masker)
#pragma warning restore CA1001
{
    private readonly ISessionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IFrameMasker _masker = masker ?? throw new ArgumentNullException(nameof(masker));

    /// <summary>
    /// One blur at a time. Each reads the image, paints it and writes it back; two interleaved would start
    /// from the same picture, and the later write would put back pixels the earlier one destroyed while
    /// masked_regions kept both. Blurs are a keypress each, so waiting costs nothing anyone can see.
    /// </summary>
    private readonly SemaphoreSlim _blurring = new(1, 1);

    /// <summary>
    /// Why a blur had no answer, kept against the command itself until <see cref="HandleAsync"/> is asked
    /// about that same command. The reason is the one the blur stopped for, never one worked out again
    /// from a second read: "too large for the pipe" means the blur was written, and must not be said of
    /// one that was not. Weak keys, so a command whose result is never asked for takes its entry with it.
    /// </summary>
    private readonly ConditionalWeakTable<BlurFrameCommand, string> _refusals = [];

    private const string NoFrame = "There is no such frame, or it has not been redacted yet.";

    /// <summary>
    /// The largest image that fits in one message once it is base64 in JSON, with room to spare under
    /// <see cref="IpcFraming.MaxMessageBytes"/>. Writing a bigger one closes the connection, which the UI
    /// sees as the service dying; a refusal with a reason is the honest answer.
    /// </summary>
    public const int LargestFrameBytes = 700 * 1024;

    /// <summary>
    /// The long edge of a filmstrip thumbnail (E-30): twice the strip's 160 px cell, so a 200% display gets
    /// real pixels. A 16:9 frame comes out 320x180, a few tens of kilobytes as JPEG.
    /// </summary>
    public const int ThumbnailEdge = 320;

    public async Task<IpcEvent?> ReplyToAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command switch
        {
            GetSessionCommand get => await SessionAsync(get, ct).ConfigureAwait(false) is ({ } session, <= IpcFraming.MaxMessageBytes)
                ? session
                : null,
            GetFrameTextCommand get => await FrameTextAsync(get, ct).ConfigureAwait(false) is ({ } text, <= IpcFraming.MaxMessageBytes)
                ? text
                : null,
            GetFrameCommand get => Fits(await _store.GetRedactedFrameImageAsync(get.FrameId, ct).ConfigureAwait(false)) is { } image
                ? new FrameLoaded { RequestId = command.RequestId, FrameId = get.FrameId, Image = image }
                : null,
            GetFrameThumbnailCommand get => await ThumbnailAsync(get, ct).ConfigureAwait(false),
            BlurFrameCommand blur => await BlurAsync(blur, ct).ConfigureAwait(false),
            _ => null,
        };
    }

    public async Task<CommandResult?> HandleAsync(IpcCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (ok, error) = command switch
        {
            GetSessionCommand get => await SessionAsync(get, ct).ConfigureAwait(false) switch
            {
                (null, _) => (false, "There is no such session."),
                (_, var bytes and > IpcFraming.MaxMessageBytes) => (false, $"The session is too large to open in Review: {bytes} bytes against the pipe's limit of {IpcFraming.MaxMessageBytes}."),
                _ => (true, null),
            },
            GetFrameTextCommand get => await FrameTextAsync(get, ct).ConfigureAwait(false) switch
            {
                (null, _) => (false, NoFrame),
                (_, var bytes and > IpcFraming.MaxMessageBytes) => (false, $"The frame's text is too large for the pipe: {bytes} bytes against a limit of {IpcFraming.MaxMessageBytes}."),
                _ => (true, null),
            },
            GetFrameCommand get => await WhyNotAsync(get.FrameId, ct).ConfigureAwait(false),
            GetFrameThumbnailCommand get => await _store.GetRedactedFrameImageAsync(get.FrameId, ct).ConfigureAwait(false) is null
                ? (false, NoFrame)
                : (false, "The frame's thumbnail could not be made from its image."),
            BlurFrameCommand blur => await WhyNotBlurredAsync(blur, ct).ConfigureAwait(false),
            SetFrameIncludedCommand set => await IncludeAsync(set, ct).ConfigureAwait(false),
            DeleteFrameCommand delete => await _store.DeleteFrameAsync(delete.FrameId, ct).ConfigureAwait(false)
                ? (true, null)
                : (false, "The frame was already gone."),
            SaveDraftCommand save => await SaveAsync(save, ct).ConfigureAwait(false),
            _ => ((bool?)null, (string?)null),
        };

        return ok is null
            ? null
            : new CommandResult { RequestId = command.RequestId, Ok = ok.Value, Error = error };
    }

    /// <summary>
    /// Paints the rectangle with the masker redaction uses, writes it, and only then hands the new bytes
    /// back. The order is the guarantee: a crash between the write and the reply leaves the disk ahead of
    /// the screen, never behind it. Destructive, and meant to be (Spec §5 S3).
    ///
    /// The rectangle arrives in the frame's own pixels, which is what the row's size and every recorded
    /// region are in. The stored image is smaller, so it is painted scaled into that image's pixels, and
    /// recorded as it arrived (S-38).
    /// </summary>
    private async Task<FrameLoaded?> BlurAsync(BlurFrameCommand blur, CancellationToken ct)
    {
        var (reply, refusal) = await PaintAsync(blur, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            _refusals.AddOrUpdate(blur, refusal);
        }

        return reply;
    }

    private async Task<(FrameLoaded? Reply, string? Refusal)> PaintAsync(BlurFrameCommand blur, CancellationToken ct)
    {
        await _blurring.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var frame = await _store.GetRedactedFrameForBlurAsync(blur.FrameId, ct).ConfigureAwait(false);
            var region = Region(blur);
            if (frame is null)
            {
                return (null, NoFrame);
            }

            if (!BlurRegion.IsInside(region, frame.Width, frame.Height))
            {
                return (null, NotInside(frame.Width, frame.Height));
            }

            // The stored frame is already at its final size, so the masker's downscale is asked for nothing:
            // an edge no image reaches means the plan is identity and only the paint happens.
            var painted = _masker.Mask(
                frame.Image,
                [BlurRegion.InStoredImage(region, frame.Width, frame.Height, frame.StoredWidth, frame.StoredHeight)],
                int.MaxValue);

            // The masker decoded the image, so it knows its real size. If that is not the size the scale
            // was worked out from, the paint is somewhere else, and writing it would record a covered
            // region over pixels that are still there. Nothing is written.
            if (painted.Width != frame.StoredWidth || painted.Height != frame.StoredHeight)
            {
                return (null, "The frame's image is not the size it was stored at, so nothing was blurred.");
            }

            try
            {
                await _store.ApplyUserBlurAsync(blur.FrameId, painted.Image, region, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Gone, or not redacted, between the read above and the write.
                return (null, NoFrame);
            }

            // Written, text and all, but the new image cannot cross the pipe. The pane keeps showing what
            // it had until the session is opened again; the words say the disk has moved on.
            return Fits(painted.Image) is { } image
                ? (new FrameLoaded { RequestId = blur.RequestId, FrameId = blur.FrameId, Image = image }, null)
                : (null, $"The blur was written, but the frame is now too large for the pipe: {painted.Image.Length} bytes against a limit of {LargestFrameBytes}. Open the session again to see it.");
        }
        finally
        {
            _blurring.Release();
        }
    }

    /// <summary>
    /// The redacted image scaled for the filmstrip (E-30), with the masker redaction uses and no regions,
    /// so nothing is painted and nothing is written. Null for a pending or missing frame (INV-1), and for
    /// an image the masker cannot read, which <see cref="HandleAsync"/> then explains rather than the
    /// fault closing the pipe.
    /// </summary>
    private async Task<FrameLoaded?> ThumbnailAsync(GetFrameThumbnailCommand get, CancellationToken ct)
    {
        if (await _store.GetRedactedFrameImageAsync(get.FrameId, ct).ConfigureAwait(false) is not { } image)
        {
            return null;
        }

        MaskedImage small;
        try
        {
            small = _masker.Mask(image, [], ThumbnailEdge);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            // GDI+ reports an image it cannot decode as one of these; OutOfMemoryException is its word for
            // a corrupt bitmap, not the process running out.
            return null;
        }

        return Fits(small.Image) is { } thumbnail
            ? new FrameLoaded { RequestId = get.RequestId, FrameId = get.FrameId, Image = thumbnail }
            : null;
    }

    private async Task<(bool, string?)> WhyNotBlurredAsync(BlurFrameCommand blur, CancellationToken ct)
    {
        // The pipe asks this about the very command ReplyToAsync just refused, so the reason is waiting.
        if (_refusals.TryGetValue(blur, out var refusal))
        {
            _refusals.Remove(blur);
            return (false, refusal);
        }

        // Asked about a blur that was never tried: say what a read can, and paint nothing.
        var frame = await _store.GetRedactedFrameForBlurAsync(blur.FrameId, ct).ConfigureAwait(false);
        return frame is null
            ? (false, NoFrame)
            : (false, BlurRegion.IsInside(Region(blur), frame.Width, frame.Height)
                ? "The blur was not tried, so nothing was blurred."
                : NotInside(frame.Width, frame.Height));
    }

    private static string NotInside(int width, int height) =>
        $"The rectangle is not inside the {width}x{height} frame, so nothing was blurred.";

    private static MaskedRegion Region(BlurFrameCommand blur) =>
        new() { X = blur.X, Y = blur.Y, Width = blur.Width, Height = blur.Height, Kind = MaskKind.UserBlur };

    /// <summary>
    /// The session as it would cross the pipe, and how many bytes that is (E-20). Every frame's OCR text is
    /// left out: on a long, text-heavy session it was most of the message, and the pane shows it for one
    /// enlarged frame at a time, which <see cref="FrameTextAsync"/> answers. What remains is measured, not
    /// estimated, because the one thing worse than refusing is writing a message the framing throws on,
    /// which closes the UI's only connection.
    /// </summary>
    private async Task<(SessionLoaded? Reply, long Bytes)> SessionAsync(GetSessionCommand get, CancellationToken ct)
    {
        if (await _store.LoadSessionAsync(get.SessionId, ct).ConfigureAwait(false) is not { } session)
        {
            return (null, 0);
        }

        var reply = new SessionLoaded
        {
            RequestId = get.RequestId,
            Session = session with { Frames = [.. session.Frames.Select(frame => frame.OcrText is null ? frame : frame with { OcrText = null })] },
        };
        return (reply, IpcFraming.MeasureBytes<IpcEvent>(reply));
    }

    /// <summary>One redacted frame's OCR text and its size on the wire; null for a pending or missing frame (INV-1).</summary>
    private async Task<(FrameTextLoaded? Reply, long Bytes)> FrameTextAsync(GetFrameTextCommand get, CancellationToken ct)
    {
        if (await _store.GetRedactedFrameTextAsync(get.FrameId, ct).ConfigureAwait(false) is not { } text)
        {
            return (null, 0);
        }

        var reply = new FrameTextLoaded { RequestId = get.RequestId, FrameId = get.FrameId, OcrText = text.OcrText };
        return (reply, IpcFraming.MeasureBytes<IpcEvent>(reply));
    }

    private async Task<(bool, string?)> WhyNotAsync(string frameId, CancellationToken ct)
    {
        var image = await _store.GetRedactedFrameImageAsync(frameId, ct).ConfigureAwait(false);
        return image switch
        {
            null => (false, NoFrame),
            { Length: > LargestFrameBytes } => (false, $"The frame is too large for the pipe: {image.Length} bytes against a limit of {LargestFrameBytes}."),
            _ => (true, null),
        };
    }

    private async Task<(bool, string?)> IncludeAsync(SetFrameIncludedCommand set, CancellationToken ct)
    {
        try
        {
            await _store.SetFrameExcludedAsync(set.FrameId, !set.Included, ct).ConfigureAwait(false);
            return (true, null);
        }
        catch (InvalidOperationException)
        {
            return (false, "There is no such frame.");
        }
    }

    private async Task<(bool, string?)> SaveAsync(SaveDraftCommand save, CancellationToken ct)
    {
        try
        {
            await _store.SaveDraftAsync(save.SessionId, save.Draft, ct).ConfigureAwait(false);
            return (true, null);
        }
        catch (InvalidOperationException)
        {
            return (false, "There is no such session.");
        }
    }

    private static byte[]? Fits(byte[]? image) => image is { Length: <= LargestFrameBytes } ? image : null;
}
