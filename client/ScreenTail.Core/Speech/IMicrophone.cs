using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Speech;

/// <summary>
/// The technician's own microphone, and the only audio source in the product (INV-9).
///
/// v1 records the technician and never the person on the other end of the call. That is not a setting or
/// a default: twelve US states require all-party consent for audio, and end-user audio is gated behind
/// the consent workflow in ST-123. There is one implementation, it opens the local capture device, and
/// there is no code path to any other source — which is what makes INV-9 a property of the design rather
/// than a rule someone has to remember.
/// </summary>
public interface IMicrophone : IAsyncDisposable
{
    /// <summary>
    /// The device, or null when there is none — no hardware, or Windows has blocked access.
    ///
    /// A device name, never audio and never content, so it is safe in the diagnostics panel a technician
    /// turns round and shows a customer (INV-10).
    /// </summary>
    string? DeviceName { get; }

    /// <summary>Samples per second. Whisper wants 16 kHz mono, and the capture side resamples to it.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Yields frames of 16-bit mono samples until cancelled. Ends immediately when there is no
    /// microphone, which is a session without narration rather than an error.
    ///
    /// Opens the device chosen in Settings, Capture when it is connected, and Windows' default
    /// communications device when it is not (ST-080 AC3), decided each time it is called: a choice made
    /// between two listens is used by the second.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<short>> ListenAsync(CancellationToken ct = default);

    /// <summary>
    /// The capture devices connected now, the default communications device marked. Ids and names,
    /// never audio. The default here, for a source that cannot tell one device from another, is the one
    /// <see cref="DeviceName"/> names.
    /// </summary>
    IReadOnlyList<MicrophoneDevice> Devices() =>
        DeviceName is { } name ? [new MicrophoneDevice(MicrophoneDevice.OnlyDeviceId, name, IsDefault: true)] : [];

    /// <summary>
    /// <see cref="ListenAsync(CancellationToken)"/> on a named device rather than the chosen one, for
    /// Settings' level meter, which listens to what the picker shows before it is saved. A device that is
    /// not connected falls back to Windows' default, as it does for a session. Opened as plain capture,
    /// not as a call, so Windows does not turn other sound down while the card is open.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<short>> ListenToAsync(string? deviceId, CancellationToken ct = default) => ListenAsync(ct);
}

/// <param name="Id">Windows' endpoint id. Not a name and not personal: it is what Settings keeps.</param>
/// <param name="Name">The friendly name, which can be personal ("Sam's headset"): shown in Settings, never logged (INV-10).</param>
/// <param name="IsDefault">Windows' default communications device.</param>
public sealed record MicrophoneDevice(string Id, string Name, bool IsDefault)
{
    /// <summary>The id a source that has only one device gives it.</summary>
    public const string OnlyDeviceId = "default";
}

/// <param name="Device">The device that opens, or null when none is connected.</param>
/// <param name="ChosenIsMissing">A device was chosen and is not connected, so <paramref name="Device"/> is Windows' default.</param>
public sealed record ResolvedMicrophone(MicrophoneDevice? Device, bool ChosenIsMissing)
{
    /// <summary>
    /// How the choice went, in words fit for a log: <c>none</c>, <c>fallback to default</c>, <c>chosen</c>
    /// or <c>default</c>. Never the device's name, which can be personal (INV-10).
    /// </summary>
    public string ForLog => Device is null ? "none" : ChosenIsMissing ? "fallback to default" : Device.IsDefault ? "default" : "chosen";
}

/// <summary>
/// Which device opens (ST-080 AC3): the chosen one when it is connected, otherwise Windows' default.
///
/// A chosen device that has gone (a headset left at home, a dock unplugged) is not a reason for a session
/// to fail or to go without narration. The default opens in its place, Settings says so, and the choice
/// is kept for when the device is back.
/// </summary>
public static class MicrophoneChoice
{
    public static ResolvedMicrophone Resolve(IReadOnlyList<MicrophoneDevice> devices, string? chosenId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var fallback = devices.FirstOrDefault(d => d.IsDefault) ?? (devices.Count > 0 ? devices[0] : null);
        if (string.IsNullOrWhiteSpace(chosenId))
        {
            return new ResolvedMicrophone(fallback, ChosenIsMissing: false);
        }

        var chosen = devices.FirstOrDefault(d => string.Equals(d.Id, chosenId, StringComparison.OrdinalIgnoreCase));
        return chosen is not null
            ? new ResolvedMicrophone(chosen, ChosenIsMissing: false)
            : new ResolvedMicrophone(fallback, ChosenIsMissing: true);
    }
}

/// <summary>
/// Turns a stretch of speech into words (ST-027). Local: the audio never leaves the machine, whatever the
/// tenant's egress settings say, because a transcript of a support call is the most sensitive thing a
/// session produces.
/// </summary>
public interface ISpeechRecogniser : IAsyncDisposable
{
    /// <summary>
    /// False while the model is still downloading. Audio offered before this is true is dropped and
    /// counted, never queued: the model is hundreds of megabytes, and a queue that grew for a whole
    /// session would then try to transcribe an hour of audio at once.
    /// </summary>
    bool Ready { get; }

    /// <returns>What was heard, or null when the recogniser produced nothing at all.</returns>
    Task<Transcribed?> TranscribeAsync(SpeechSegment segment, ReadOnlyMemory<short> audio, CancellationToken ct = default);
}

/// <summary>
/// The session being recorded right now, as narration needs to know it.
/// </summary>
/// <param name="SessionId">
/// Which session is listening. A segment is stamped with it when it is cut from the microphone, and is
/// only ever written into that session: one customer's words must not reach the next customer's ticket
/// because the model was still thinking when the sessions changed over.
/// </param>
/// <param name="AtMs">
/// How far into the session this moment is, on the session's own clock (the one clicks and frames are
/// stamped with), so a segment lands on the timeline where it was said.
/// </param>
public readonly record struct NarrationSession(string SessionId, long AtMs);

/// <param name="heardIn">The session the segment was heard in, from <see cref="NarrationSession.SessionId"/>.</param>
/// <param name="segment">What was said, on that session's clock.</param>
/// <returns>
/// False when the session refused it, which is the ordinary answer once that session is over or discarded
/// (INV-6) and not a failure.
/// </returns>
public delegate Task<bool> AppendTranscript(string heardIn, TranscriptSegment segment, CancellationToken ct);
