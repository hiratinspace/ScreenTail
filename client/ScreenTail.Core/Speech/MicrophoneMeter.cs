using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Speech;

/// <summary>What the level meter says: a state from <see cref="MicrophoneLevelStates"/>, a level from 0 to 1, and the device listened to.</summary>
public sealed record MeterReading(string State, double Level, string? DeviceId)
{
    public static MeterReading Closed { get; } = new(MicrophoneLevelStates.Closed, 0, null);
}

/// <summary>How loud a stretch of samples is, as a meter shows it.</summary>
public static class MicrophoneLevel
{
    /// <summary>The quietest level the meter moves for. Room noise sits near it, speech well above.</summary>
    public const double FloorDecibels = -60;

    /// <summary>
    /// The peak of <paramref name="samples"/> on a decibel scale, 0 at <see cref="FloorDecibels"/> and
    /// below, 1 at full scale. A number: the samples are read once and not kept.
    /// </summary>
    public static double Of(ReadOnlySpan<short> samples)
    {
        var peak = 0;
        foreach (var sample in samples)
        {
            var magnitude = sample == short.MinValue ? 32768 : Math.Abs((int)sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        if (peak == 0)
        {
            return 0;
        }

        var decibels = 20 * Math.Log10(peak / 32768.0);
        return Math.Clamp((decibels - FloorDecibels) / -FloorDecibels, 0, 1);
    }
}

/// <summary>
/// Settings, Capture's live level meter, on the service's side (ST-080 AC3, Spec §5 S5).
///
/// <b>A new time the microphone opens outside a session,</b> so it is held to more than the recorder is:
/// <list type="bullet">
/// <item>It opens only when asked, and each request is a lease (<see cref="DefaultLease"/>). The card asks
/// about ten times a second while it is on screen; when the asking stops, because the card was hidden,
/// the window went to the background, or the UI hung or died, the device is closed by itself. Nothing on
/// the UI's side has to remember to close it.</item>
/// <item>It never opens while a session holds the microphone (recording, paused, suppressed or
/// finishing), and a session that starts while it is open closes it. The recorder and the pill are left
/// exactly as they were: the meter does not change the session's state, so the pill and the tray never
/// show it as capture (INV-4).</item>
/// <item>Levels only. Each frame of audio is turned into one number and dropped. Nothing heard is kept,
/// queued, transcribed, written or sent, and this class holds no field that could keep a sample: it has no
/// store, no recogniser and no buffer (INV-9, <c>MicrophoneMeterTests</c> pins that). Nothing is logged.</item>
/// <item>No timer while it is closed: an idle service makes no wakeups for it (ST-031).</item>
/// </list>
/// </summary>
public sealed class MicrophoneMeter : IAsyncDisposable
{
    /// <summary>How long one request keeps the device open. The card asks every 100 ms.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a device that would not open, or stopped, is left alone before it is tried again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    // A device that does not let go within this is abandoned to its own cancellation rather than waited
    // for: a request must not hang on a driver.
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(2);

    private readonly IMicrophone _microphone;
    private readonly Func<bool> _sessionHoldsMicrophone;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lease;

    // Opening and closing, one at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // What the listening loop writes and a request reads.
    private readonly Lock _state = new();

    private CancellationTokenSource? _listening;
    private Task? _loop;
    private ITimer? _expiry;
    private string? _askedFor;
    private bool _disposed;

    private DateTimeOffset _leaseUntil;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private string _status = MicrophoneLevelStates.Closed;
    private double _level;
    private string? _deviceId;

    /// <param name="sessionHoldsMicrophone">
    /// True while a session is recording, paused, suppressed or finishing: the meter does not open the
    /// device then, so a paused pill never sits beside a lit microphone.
    /// </param>
    /// <param name="lease">How long one request keeps the device open; <see cref="DefaultLease"/> when null.</param>
    public MicrophoneMeter(IMicrophone microphone, Func<bool> sessionHoldsMicrophone, TimeProvider? time = null, TimeSpan? lease = null)
    {
        _microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));
        _sessionHoldsMicrophone = sessionHoldsMicrophone ?? throw new ArgumentNullException(nameof(sessionHoldsMicrophone));
        _time = time ?? TimeProvider.System;
        _lease = lease ?? DefaultLease;
    }

    /// <summary>Whether the device is open for the meter right now.</summary>
    public bool IsOpen => _loop is { IsCompleted: false };

    /// <summary>How many times the meter has opened the device. A count, for the tests and nothing else.</summary>
    public int Opened { get; private set; }

    /// <summary>
    /// The level now, opening the device on the first request and keeping it open for one more lease on
    /// each. Refused, and anything open closed, while a session holds the microphone.
    /// </summary>
    /// <param name="deviceId">The device the picker shows, saved or not; null is Windows' default.</param>
    public async Task<MeterReading> ReadAsync(string? deviceId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                // The service is stopping. Nothing opens, and the card is told so rather than thrown at.
                return MeterReading.Closed;
            }

            if (_sessionHoldsMicrophone())
            {
                await CloseAsync().ConfigureAwait(false);
                return new MeterReading(MicrophoneLevelStates.Session, 0, null);
            }

            var wanted = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
            var now = _time.GetUtcNow();
            if (_loop is not null && !string.Equals(_askedFor, wanted, StringComparison.OrdinalIgnoreCase))
            {
                // Another device chosen in the picker: this one is let go of before the next opens.
                await CloseAsync().ConfigureAwait(false);
            }

            CollectEnded();
            if (_loop is not null)
            {
                lock (_state)
                {
                    _leaseUntil = now + _lease;
                    return new MeterReading(_status, _level, _deviceId);
                }
            }

            if (now < _retryAt && string.Equals(_askedFor, wanted, StringComparison.OrdinalIgnoreCase))
            {
                lock (_state)
                {
                    return new MeterReading(_status, 0, _deviceId);
                }
            }

            return Open(wanted, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The card left the screen: the device is closed now rather than when the lease runs out.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                await CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes the device once its lease has run out. Called by the meter's own timer while it is open, and
    /// on every frame of audio; public so that a test can drive it with a clock of its own.
    /// </summary>
    public async Task ExpireAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            CollectEnded();
            if (_loop is not null && Expired(_time.GetUtcNow()))
            {
                await CloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The session's state changed: a session that now holds the microphone closes the meter at once.</summary>
    public async Task SessionChangedAsync()
    {
        if (!_sessionHoldsMicrophone())
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await CloseAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private MeterReading Open(string? wanted, DateTimeOffset now)
    {
        _askedFor = wanted;
        var resolved = MicrophoneChoice.Resolve(_microphone.Devices(), wanted);
        if (resolved.Device is null)
        {
            lock (_state)
            {
                _status = MicrophoneLevelStates.NoMicrophone;
                _level = 0;
                _deviceId = null;
            }

            _retryAt = now + RetryAfter;
            return new MeterReading(MicrophoneLevelStates.NoMicrophone, 0, null);
        }

        lock (_state)
        {
            _status = MicrophoneLevelStates.Opening;
            _level = 0;
            _deviceId = resolved.Device.Id;
            _leaseUntil = now + _lease;
        }

        _retryAt = DateTimeOffset.MinValue;
        _listening = new CancellationTokenSource();
        var token = _listening.Token;
        var device = resolved.Device.Id;
        _loop = Task.Run(() => ListenAsync(device, token), CancellationToken.None);

        // Armed only while open, and checked twice a lease, so a quiet device with no frames arriving
        // is still closed on time.
        var check = _lease / 2;
        _expiry = _time.CreateTimer(static meter => _ = ((MicrophoneMeter)meter!).ExpireAsync(), this, check, check);
        Opened++;
        return new MeterReading(MicrophoneLevelStates.Opening, 0, device);
    }

    private async Task ListenAsync(string deviceId, CancellationToken ct)
    {
        var expired = false;
        try
        {
            await foreach (var frame in _microphone.ListenToAsync(deviceId, ct).ConfigureAwait(false))
            {
                // One number from the frame, and the frame goes: it is a local of this loop and nothing
                // else ever sees it.
                var level = MicrophoneLevel.Of(frame.Span);
                lock (_state)
                {
                    if (_time.GetUtcNow() > _leaseUntil)
                    {
                        expired = true;
                    }
                    else
                    {
                        _status = MicrophoneLevelStates.Listening;
                        _level = level;
                    }
                }

                if (expired)
                {
                    // Leaving the loop disposes the enumerator, which closes the device.
                    break;
                }
            }

            if (!expired && !ct.IsCancellationRequested)
            {
                // The device stopped by itself: unplugged, or Windows took it away.
                Ended(MicrophoneLevelStates.Unavailable);
            }
            else if (expired)
            {
                Ended(MicrophoneLevelStates.Closed);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closed on purpose.
        }
#pragma warning disable CA1031 // A meter that fails says so on the card; it must not take the service with it.
        catch (Exception)
#pragma warning restore CA1031
        {
            // The type is not even logged: there is nothing a technician can do with it that the card's
            // sentence does not already say.
            Ended(MicrophoneLevelStates.Unavailable);
        }
    }

    private void Ended(string status)
    {
        lock (_state)
        {
            _status = status;
            _level = 0;
        }
    }

    private bool Expired(DateTimeOffset now)
    {
        lock (_state)
        {
            return now > _leaseUntil;
        }
    }

    /// <summary>A loop that ended by itself is put away, its timer with it; a device that failed waits <see cref="RetryAfter"/>.</summary>
    private void CollectEnded()
    {
        if (_loop is not { IsCompleted: true })
        {
            return;
        }

        Release();
        lock (_state)
        {
            if (_status == MicrophoneLevelStates.Unavailable)
            {
                _retryAt = _time.GetUtcNow() + RetryAfter;
            }
        }
    }

    private async Task CloseAsync()
    {
        if (_loop is { } loop)
        {
            await _listening!.CancelAsync().ConfigureAwait(false);
            try
            {
                await loop.WaitAsync(CloseWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The driver is slow to let go; it has been told to, and the loop ends on its own.
            }
        }

        Release();
        lock (_state)
        {
            _status = MicrophoneLevelStates.Closed;
            _level = 0;
            _deviceId = null;
        }

        _askedFor = null;
        _retryAt = DateTimeOffset.MinValue;
    }

    private void Release()
    {
        _expiry?.Dispose();
        _expiry = null;
        _listening?.Dispose();
        _listening = null;
        _loop = null;
    }
}
