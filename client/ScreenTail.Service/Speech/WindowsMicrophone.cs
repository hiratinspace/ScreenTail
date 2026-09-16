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
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMicrophone : IMicrophone
{
    private WasapiRecorder? _recorder;

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

        var recorder = Open();
        if (recorder is null)
        {
            yield break;
        }

        _recorder = recorder;
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

    public async ValueTask DisposeAsync()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is not null)
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
        }
    }

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
