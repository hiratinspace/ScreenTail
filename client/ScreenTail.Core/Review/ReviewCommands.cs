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
public sealed class ReviewCommands(ISessionStore store, IFrameMasker masker)
{
    private readonly ISessionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IFrameMasker _masker = masker ?? throw new ArgumentNullException(nameof(masker));

    /// <summary>
    /// The largest image that fits in one message once it is base64 in JSON, with room to spare under
    /// <see cref="IpcFraming.MaxMessageBytes"/>. Writing a bigger one closes the connection, which the UI
    /// sees as the service dying; a refusal with a reason is the honest answer.
    /// </summary>
    public const int LargestFrameBytes = 700 * 1024;

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
                (null, _) => (false, "There is no such frame, or it has not been redacted yet."),
                (_, var bytes and > IpcFraming.MaxMessageBytes) => (false, $"The frame's text is too large for the pipe: {bytes} bytes against a limit of {IpcFraming.MaxMessageBytes}."),
                _ => (true, null),
            },
            GetFrameCommand get => await WhyNotAsync(get.FrameId, ct).ConfigureAwait(false),
            BlurFrameCommand blur => await WhyNotAsync(blur.FrameId, ct).ConfigureAwait(false),
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
    /// </summary>
    private async Task<FrameLoaded?> BlurAsync(BlurFrameCommand blur, CancellationToken ct)
    {
        var image = await _store.GetRedactedFrameImageAsync(blur.FrameId, ct).ConfigureAwait(false);
        if (image is null)
        {
            return null;
        }

        var region = new MaskedRegion { X = blur.X, Y = blur.Y, Width = blur.Width, Height = blur.Height, Kind = MaskKind.UserBlur };

        // The stored frame is already at its final size, so the masker's downscale is asked for nothing:
        // an edge no image reaches means the plan is identity and only the paint happens.
        var painted = _masker.Mask(image, [region], int.MaxValue);
        try
        {
            await _store.ApplyUserBlurAsync(blur.FrameId, painted.Image, region, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Gone, or not redacted, between the read above and the write. HandleAsync says so.
            return null;
        }

        return Fits(painted.Image) is { } image2
            ? new FrameLoaded { RequestId = blur.RequestId, FrameId = blur.FrameId, Image = image2 }
            : null;
    }

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
            null => (false, "There is no such frame, or it has not been redacted yet."),
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
