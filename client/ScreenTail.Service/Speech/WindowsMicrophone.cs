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
/// <b>Which device.</b> The one chosen in Settings, Capture when it is connected, and otherwise Windows'
/// default communications device: the role Windows uses for the headset a technician actually talks
/// into, rather than whatever the default playback device happens to be paired with (ST-080 AC3). It is
/// decided each time a listen begins, from the devices connected at that moment, so a choice saved
/// between two listens is used by the second and a headset left at home falls back to the default
/// without failing anything (<see cref="MicrophoneChoice"/>). The device is opened by its endpoint
/// rather than left to the builder, so the device named in Settings and in the diagnostics panel is the
/// one that opens.
///
/// A session's listen opens in communications mode, which enables the endpoint's own echo cancellation
/// where the driver offers it; the level meter's does not, so Settings never looks like a call to Windows. The device is asked for 16 kHz mono because that is what the model wants, and shared mode
/// is entitled to refuse: whatever comes back is converted by <see cref="AudioConversion"/>, which is
/// tested without a sound card. Nothing above this layer ever sees another format.
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
/// enumeration ends. Settings' level meter listens the same way, through <see cref="ListenToAsync"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMicrophone : IMicrophone
{
    private readonly Func<string?> _chosen;
    private string? _deviceName;
    private string? _deviceId;

    /// <param name="chosen">The device id saved in Settings, Capture, read when a listen begins; null (or none given) is Windows' default.</param>
    public WindowsMicrophone(Func<string?>? chosen = null)
    {
        _chosen = chosen ?? (static () => null);
        _ = Devices();
    }

    public int SampleRate => AudioConversion.TargetRate;

    /// <summary>
    /// The device a listen would open, as of the last look at what is connected. A device name, never
    /// audio and never content, so the diagnostics panel can show it (INV-10).
    /// </summary>
    public string? DeviceName => Volatile.Read(ref _deviceName);

    /// <summary>Windows' id for the same device as <see cref="DeviceName"/>. Not personal, so a test can compare it where a name would end up in a public log.</summary>
    public string? DeviceId => Volatile.Read(ref _deviceId);

    /// <summary>The capture endpoints that are active now, Windows' default communications device marked. Empty when Windows will not say.</summary>
    public IReadOnlyList<MicrophoneDevice> Devices()
    {
        var devices = Enumerate();
        Remember(devices);
        return devices;
    }

    /// <summary>The recorder's listen: the chosen device, in communications mode for the endpoint's echo cancellation.</summary>
    public IAsyncEnumerable<ReadOnlyMemory<short>> ListenAsync(CancellationToken ct = default) => ListenCoreAsync(_chosen(), communications: true, ct);

    /// <summary>
    /// The level meter's listen. Not in communications mode: opening a communications stream is what
    /// tells Windows a call has started, and it may then turn other sound down while the card is open.
    /// </summary>
    public IAsyncEnumerable<ReadOnlyMemory<short>> ListenToAsync(string? deviceId, CancellationToken ct = default) => ListenCoreAsync(deviceId, communications: false, ct);

    private async IAsyncEnumerable<ReadOnlyMemory<short>> ListenCoreAsync(string? deviceId, bool communications, [EnumeratorCancellation] CancellationToken ct)
    {
        var devices = Enumerate();
        var resolved = MicrophoneChoice.Resolve(devices, deviceId);
        Remember(devices);
        if (resolved.Device is null)
        {
            yield break;
        }

        // Closed when this enumeration ends, whether the consumer ran it out, cancelled it or disposed
        // the enumerator early: that last one is how the recorder ends a stretch, and how the meter
        // lets go when its lease runs out. Either must close the device whatever NAudio's capture loop
        // does about it. The endpoint outlives the recorder built on it.
        using var endpoint = Find(resolved.Device.Id);
        if (endpoint is null)
        {
            yield break;
        }

        await using var recorder = Open(endpoint, communications);
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

    private void Remember(IReadOnlyList<MicrophoneDevice> devices)
    {
        var opens = MicrophoneChoice.Resolve(devices, _chosen()).Device;
        Volatile.Write(ref _deviceName, opens?.Name);
        Volatile.Write(ref _deviceId, opens?.Id);
    }

    private static List<MicrophoneDevice> Enumerate()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string? defaultId = null;
            if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications, out var fallback) && fallback is not null)
            {
                using (fallback)
                {
                    defaultId = fallback.ID;
                }
            }

            var found = new List<MicrophoneDevice>();
            using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var endpoint in endpoints)
            {
                using (endpoint)
                {
                    found.Add(new MicrophoneDevice(endpoint.ID, endpoint.FriendlyName, string.Equals(endpoint.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
            }

            return found;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Blocked by privacy settings, or no audio stack at all. Both mean "no microphone".
            return [];
        }
    }

    private static MMDevice? Find(string id)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(id);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Unplugged between the look and the open.
            return null;
        }
    }

    /// <summary>
    /// Opens the endpoint, asking for the format the model wants and settling for what it is given.
    ///
    /// A device that refuses 16 kHz mono is ordinary — plenty of endpoints only offer their configured
    /// mix format — and the conversion is ready for it, so the refusal is retried without the request
    /// rather than reported as a machine with no microphone.
    /// </summary>
    private static WasapiRecorder? Open(MMDevice endpoint, bool communications)
    {
        foreach (var asking in new[] { true, false })
        {
            try
            {
                var builder = new WasapiRecorderBuilder().WithDevice(endpoint);
                if (communications)
                {
                    builder = builder.WithCommunicationsMode();
                }

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
