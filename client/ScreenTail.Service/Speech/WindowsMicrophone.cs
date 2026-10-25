using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenTail.Core.Speech;

namespace ScreenTail.Service.Speech;

/// <summary>
/// The technician's microphone (ST-027, INV-9).
///
/// Opened in communications mode, which is what Windows uses to pick the headset a technician is
/// actually talking into rather than whatever the default playback device happens to be paired with, and
/// what enables the endpoint's own echo cancellation where the driver offers it.
///
/// The device is asked for 16 kHz mono because that is what the model wants, and shared mode is entitled
/// to refuse: whatever comes back is converted by <see cref="AudioConversion"/>, which is tested without
/// a sound card. Nothing above this layer ever sees another format.
///
/// <b>No microphone is not an error.</b> A machine without one, or one where Windows has blocked access,
/// still records clicks and screenshots (AC2): losing narration costs a better note, and stopping here
/// would cost the session.
///
/// <b>Each listen owns its device.</b> The recorder opens the microphone once per recording stretch and
/// closes it by disposing the enumerator at the end of the stretch (#122), which is what keeps Windows'
/// microphone-in-use indicator off a customer's screen between sessions. The recorder built for a stretch
/// used to be kept in a field and disposed only with this object, so every pause and every password field
/// left the previous device open with its capture thread running and the indicator lit, and only the last
/// one was ever closed (2026-10-12 review). It is a local of the enumeration now, released however the
/// enumeration ends.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMicrophone : IMicrophone
{
    public WindowsMicrophone()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            {
                return;
            }

            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            DeviceName = device.FriendlyName;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Blocked by privacy settings, or no audio stack at all. Both mean "no microphone".
            DeviceName = null;
        }
    }

    public int SampleRate => AudioConversion.TargetRate;

    /// <summary>A device name, never audio and never content, so the diagnostics panel can show it (INV-10).</summary>
    public string? DeviceName { get; }

    public async IAsyncEnumerable<ReadOnlyMemory<short>> ListenAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (DeviceName is null)
        {
            yield break;
        }

        // Closed when this enumeration ends, whether the consumer ran it out, cancelled it or disposed
        // the enumerator early: that last one is how the recorder ends a stretch, and it must close the
        // device whatever NAudio's capture loop does about it.
        await using var recorder = Open();
        if (recorder is null)
        {
            yield break;
        }

        var format = Describe(recorder.WaveFormat);
        await foreach (var buffer in recorder.CaptureAsync(ct).ConfigureAwait(false))
        {
            var samples = AudioConversion.ToMono16k(buffer.Data.Span, format);
            if (!samples.IsEmpty)
            {
                yield return samples;
            }
        }
    }

    /// <summary>Nothing held between listens: each enumeration of <see cref="ListenAsync"/> closes its own device.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Opens the endpoint, asking for the format the model wants and settling for what it is given.
    ///
    /// A device that refuses 16 kHz mono is ordinary — plenty of endpoints only offer their configured
    /// mix format — and the conversion is ready for it, so the refusal is retried without the request
    /// rather than reported as a machine with no microphone.
    /// </summary>
    private static WasapiRecorder? Open()
    {
        foreach (var asking in new[] { true, false })
        {
            try
            {
                var builder = new WasapiRecorderBuilder().WithCommunicationsMode();
                if (asking)
                {
                    builder = builder.WithFormat(new WaveFormat(AudioConversion.TargetRate, 16, 1));
                }

                return builder.Build();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                // Try again without the format request; if that fails too, the session has no narration.
            }
        }

        return null;
    }

    private static AudioFormat Describe(WaveFormat format) => new(
        format.SampleRate,
        format.Channels,
        format.BitsPerSample,
        format.Encoding == WaveFormatEncoding.IeeeFloat);
}
