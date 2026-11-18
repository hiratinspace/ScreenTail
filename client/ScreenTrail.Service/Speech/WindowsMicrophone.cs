using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenTrail.Core.Speech;

namespace ScreenTrail.Service.Speech;

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
///
/// <b>Devices come and go during a session (2026-11-18).</b> Windows tells this class when a capture
/// device is added, removed, enabled or disabled, or the default communications device changes; it only
/// notes that something changed, because the notification arrives on Windows' own thread, where looking
/// at the devices is not safe. The recorder asks <see cref="DeviceMoved"/> with each chunk, which looks
/// then, on the recorder's thread, and only after a change. A device that is unplugged mid-listen ends
/// the listen rather than throwing, and the recorder opens whatever is connected a moment later
/// (<see cref="Reopens"/>). Before this, an unplugged headset ended narration until the service
/// restarted, and a headset plugged in was not used until the session paused.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMicrophone : IMicrophone
{
    private readonly Func<string?> _chosen;
    private string? _deviceName;
    private string? _deviceId;

    /// <summary>Windows' device notifications, or null where they could not be had (then nothing moves mid-listen; a stop still reopens).</summary>
    private readonly MMDeviceEnumerator? _watching;
    private readonly MMDeviceNotificationClient? _notifications;

    /// <summary>Set by a notification or <see cref="Recheck"/>, cleared when <see cref="DeviceMoved"/> looks.</summary>
    private int _changed;

    /// <summary>The device the session's listen opened, or null while none is open.</summary>
    private string? _openId;

    /// <summary>Whether a look since that listen opened found another device to use. Kept until the next listen opens.</summary>
    private int _moved;

    /// <param name="chosen">The device id saved in Settings, Capture, read when a listen begins; null (or none given) is Windows' default.</param>
    public WindowsMicrophone(Func<string?>? chosen = null)
    {
        _chosen = chosen ?? (static () => null);
        _ = Devices();
        try
        {
            _watching = new MMDeviceEnumerator();

            // Not marshalled to a synchronisation context: the service has none, and the handlers only set a flag.
            _notifications = _watching.CreateNotificationClient(false);
            _notifications.DeviceAdded += (_, _) => Recheck();
            _notifications.DeviceRemoved += (_, _) => Recheck();
            _notifications.DeviceStateChanged += (_, _) => Recheck();
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Capture && e.Role == Role.Communications)
                {
                    Recheck();
                }
            };
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // No notifications: a device change mid-listen waits for the next listen, as it did before.
            _notifications?.Dispose();
            _watching?.Dispose();
            _notifications = null;
            _watching = null;
        }
    }

    /// <summary>A device can come back: the recorder opens whatever is connected after one stops, or when there was none.</summary>
    public bool Reopens => true;

    /// <summary>
    /// Look again at which device a listen would open, on the next <see cref="DeviceMoved"/>. Called by a
    /// device notification, and by the host when the choice in Settings changes, so a new choice is used
    /// at the next pause rather than the next session.
    /// </summary>
    public void Recheck() => Interlocked.Exchange(ref _changed, 1);

    /// <summary>
    /// Whether the session's open listen should move to another device. Cheap until something changed:
    /// then one look at the connected devices, on the caller's thread.
    /// </summary>
    public bool DeviceMoved
    {
        get
        {
            if (Interlocked.Exchange(ref _changed, 0) != 0 && Volatile.Read(ref _openId) is { } open)
            {
                var devices = Enumerate();
                Remember(devices);
                var now = MicrophoneChoice.Resolve(devices, _chosen()).Device;

                // Nothing connected is not a move: the open device stopping ends the listen on its own.
                if (now is not null && !string.Equals(now.Id, open, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Exchange(ref _moved, 1);
                }
            }

            return Volatile.Read(ref _moved) != 0;
        }
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

        if (communications)
        {
            // The session's listen. Only it moves when the device does; the meter follows the picker.
            Interlocked.Exchange(ref _moved, 0);
            Interlocked.Exchange(ref _changed, 0);
            Volatile.Write(ref _openId, resolved.Device.Id);
        }

        try
        {
            var format = Describe(recorder.WaveFormat);
            var captured = recorder.CaptureAsync(ct).GetAsyncEnumerator(ct);
            await using (captured.ConfigureAwait(false))
            {
                while (true)
                {
                    bool more;
                    try
                    {
                        more = await captured.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is COMException or InvalidOperationException)
                    {
                        // The device went away under the capture (AUDCLNT_E_DEVICE_INVALIDATED): unplugged,
                        // disabled, or taken exclusively. The listen ends as if the device had stopped,
                        // and the recorder opens whatever is connected next. Thrown, it ended narration.
                        more = false;
                    }

                    if (!more)
                    {
                        break;
                    }

                    var samples = AudioConversion.ToMono16k(captured.Current.Data.Span, format);
                    if (!samples.IsEmpty)
                    {
                        yield return samples;
                    }
                }
            }
        }
        finally
        {
            if (communications)
            {
                Volatile.Write(ref _openId, null);
            }
        }
    }

    /// <summary>Stops Windows' device notifications. Each enumeration of <see cref="ListenAsync"/> closes its own device.</summary>
    public ValueTask DisposeAsync()
    {
        _notifications?.Dispose();
        _watching?.Dispose();
        return ValueTask.CompletedTask;
    }

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
    /// A device that refuses 16 kHz mono is ordinary (plenty of endpoints only offer their configured
    /// mix format), and the conversion is ready for it, so the refusal is retried without the request
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
