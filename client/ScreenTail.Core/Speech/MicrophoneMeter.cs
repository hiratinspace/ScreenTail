using ScreenTail.Core.Sessions;
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
/// about ten times a second while it is looked at, and each request waits for the UI thread to answer
/// first. When the asking stops, because the card was hidden, the window went to the background, the
/// screen was locked, or the UI hung or died, the device is closed by itself within a few seconds.
/// Nothing on the UI's side has to remember to close it.</item>
/// <item>It is never open for longer than <see cref="MaxOpen"/> at a stretch, however the asking goes. It
/// then stays closed until the card is put away and looked at again (a stop), so a Settings window left
/// in front all afternoon does not keep the microphone open all afternoon.</item>
/// <item>It never opens while a session holds the microphone (<see cref="HeldBy"/>: recording, paused,
/// suppressed or finishing), and a session that starts closes it. The recorder and the pill are left
/// exactly as they were: the meter does not change the session's state, so the pill and the tray never
/// show it as capture (INV-4).</item>
/// <item>Levels only. Each frame of audio is turned into one number and dropped. Nothing heard is kept,
/// queued, transcribed, written or sent, and this class holds no field that could keep a sample: it has no
/// store, no recogniser and no buffer (INV-9, <c>MicrophoneMeterTests</c> pins that). Nothing is logged.</item>
/// <item>No timer while it is closed: an idle service makes no wakeups for it (ST-031).</item>
/// <item>Its clock is the monotonic one, so the wall clock moving (a time zone, a sync) neither closes it
/// early nor keeps it open.</item>
/// </list>
/// </summary>
public sealed class MicrophoneMeter : IAsyncDisposable
{
    /// <summary>How long one request keeps the device open. The card asks every 100 ms.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a device that would not open, or stopped, is left alone before it is tried again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    /// <summary>The longest the meter keeps the device open at a stretch, whatever the asking (an owner decision; 2026-10-31 review).</summary>
    public static readonly TimeSpan MaxOpen = TimeSpan.FromMinutes(5);

    /// <summary>How long a close waits for a driver to let go before the listen is abandoned to its own cancellation.</summary>
    public static readonly TimeSpan DefaultCloseWait = TimeSpan.FromSeconds(2);

    private readonly IMicrophone _microphone;
    private readonly Func<bool> _sessionHoldsMicrophone;
    private readonly TimeProvider _time;
    private readonly TimeSpan _lease;
    private readonly TimeSpan _cap;
    private readonly TimeSpan _closeWait;

    // Opening and closing, one at a time.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // What the listening loop writes and a request reads.
    private readonly Lock _state = new();

    private CancellationTokenSource? _listening;
    private Task? _loop;

    // A listen that was told to stop and did not within the close wait. Nothing opens until it has gone,
    // so a slow driver never ends up with two streams open on it.
    private Task? _abandoned;
    private ITimer? _expiry;
    private string? _askedFor;
    private bool _disposed;
    private bool _retrying;
    private long _retryFrom;

    private int _generation;
    private long _leaseFrom;
    private long _openedAt;
    private bool _timedOut;
    private string _status = MicrophoneLevelStates.Closed;
    private double _level;
    private string? _deviceId;

    /// <param name="sessionHoldsMicrophone">
    /// True while a session is recording, paused, suppressed or finishing: the meter does not open the
    /// device then, so a paused pill never sits beside a lit microphone.
    /// </param>
    /// <param name="lease">How long one request keeps the device open; <see cref="DefaultLease"/> when null.</param>
    /// <param name="cap">The longest it stays open at a stretch; <see cref="MaxOpen"/> when null.</param>
    /// <param name="closeWait">How long a close waits for the device; <see cref="DefaultCloseWait"/> when null.</param>
    public MicrophoneMeter(IMicrophone microphone, Func<bool> sessionHoldsMicrophone, TimeProvider? time = null, TimeSpan? lease = null, TimeSpan? cap = null, TimeSpan? closeWait = null)
    {
        _microphone = microphone ?? throw new ArgumentNullException(nameof(microphone));
        _sessionHoldsMicrophone = sessionHoldsMicrophone ?? throw new ArgumentNullException(nameof(sessionHoldsMicrophone));
        _time = time ?? TimeProvider.System;
        _lease = lease ?? DefaultLease;
        _cap = cap ?? MaxOpen;
        _closeWait = closeWait ?? DefaultCloseWait;
    }

    /// <summary>Whether this state of the session holds the microphone, so the meter may not open it.</summary>
    public static bool HeldBy(SessionState state) =>
        state is SessionState.Recording or SessionState.Paused or SessionState.Suppressed or SessionState.Finalizing;

    /// <summary>
    /// The meter as the service runs it: held off by <paramref name="machine"/>'s session, and closed the
    /// moment the machine moves into a state that holds the microphone.
    /// </summary>
    public static MicrophoneMeter ForMachine(IMicrophone microphone, SessionMachine machine, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        var meter = new MicrophoneMeter(microphone, () => HeldBy(machine.State), time);
        machine.StateChanged += changed => _ = meter.SessionChangedAsync();
        return meter;
    }

    /// <summary>Whether the device is open for the meter right now.</summary>
    public bool IsOpen => _loop is { IsCompleted: false } || _abandoned is { IsCompleted: false };

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
            if (_loop is not null && !string.Equals(_askedFor, wanted, StringComparison.OrdinalIgnoreCase))
            {
                // Another device chosen in the picker: this one is let go of before the next opens.
                await CloseAsync().ConfigureAwait(false);
            }

            CollectEnded();
            lock (_state)
            {
                if (_timedOut)
                {
                    return new MeterReading(MicrophoneLevelStates.TimedOut, 0, null);
                }

                if (_loop is not null)
                {
                    _leaseFrom = _time.GetTimestamp();
                    return new MeterReading(_status, _level, _deviceId);
                }
            }

            if (_abandoned is { IsCompleted: false })
            {
                // The last listen has not let go yet. Asking again is not a reason to open a second one.
                return MeterReading.Closed;
            }

            _abandoned = null;
            if (_retrying && _time.GetElapsedTime(_retryFrom) < RetryAfter && string.Equals(_askedFor, wanted, StringComparison.OrdinalIgnoreCase))
            {
                lock (_state)
                {
                    return new MeterReading(_status, 0, _deviceId);
                }
            }

            return Open(wanted);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The card left the screen: the device is closed now rather than when the lease runs out, and a meter
    /// that reached <see cref="MaxOpen"/> may open again the next time the card is looked at.
    /// </summary>
    public Task StopAsync() => CloseUnderGateAsync(cardPutAway: true);

    /// <summary>
    /// Closes the device once its lease has run out, or once it has been open for <see cref="MaxOpen"/>.
    /// Called by the meter's own timer while it is open, and checked on every frame of audio; public so
    /// that a test can drive it with a clock of its own.
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
            if (_loop is null)
            {
                return;
            }

            bool capped;
            bool expired;
            lock (_state)
            {
                capped = _time.GetElapsedTime(_openedAt) > _cap;
                expired = _time.GetElapsedTime(_leaseFrom) > _lease;
            }

            if (capped || expired)
            {
                await CloseAsync().ConfigureAwait(false);
                if (capped)
                {
                    lock (_state)
                    {
                        _timedOut = true;
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The session's state changed: a session that now holds the microphone closes the meter at once. A
    /// meter that reached <see cref="MaxOpen"/> stays that way: only the card being put away resets it.
    /// </summary>
    public async Task SessionChangedAsync()
    {
        if (!_sessionHoldsMicrophone())
        {
            return;
        }

        await CloseUnderGateAsync(cardPutAway: false).ConfigureAwait(false);
    }

    private async Task CloseUnderGateAsync(bool cardPutAway)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await CloseAsync().ConfigureAwait(false);
            if (cardPutAway)
            {
                lock (_state)
                {
                    _timedOut = false;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
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

    private MeterReading Open(string? wanted)
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

            _retrying = true;
            _retryFrom = _time.GetTimestamp();
            return new MeterReading(MicrophoneLevelStates.NoMicrophone, 0, null);
        }

        int generation;
        lock (_state)
        {
            generation = ++_generation;
            _status = MicrophoneLevelStates.Opening;
            _level = 0;
            _deviceId = resolved.Device.Id;
            _leaseFrom = _time.GetTimestamp();
            _openedAt = _leaseFrom;
        }

        _retrying = false;
        _listening = new CancellationTokenSource();
        var token = _listening.Token;
        var device = resolved.Device.Id;
        _loop = Task.Run(() => ListenAsync(device, generation, token), CancellationToken.None);

        // Armed only while open, and checked twice a lease, so a quiet device with no frames arriving
        // is still closed on time.
        var check = _lease / 2;
        _expiry = _time.CreateTimer(static meter => _ = ((MicrophoneMeter)meter!).ExpireAsync(), this, check, check);
        Opened++;
        return new MeterReading(MicrophoneLevelStates.Opening, 0, device);
    }

    private async Task ListenAsync(string deviceId, int generation, CancellationToken ct)
    {
        string? ended = null;
        try
        {
            await foreach (var frame in _microphone.ListenToAsync(deviceId, ct).ConfigureAwait(false))
            {
                // One number from the frame, and the frame goes: it is a local of this loop and nothing
                // else ever sees it.
                var level = MicrophoneLevel.Of(frame.Span);
                lock (_state)
                {
                    if (generation != _generation)
                    {
                        ended = MicrophoneLevelStates.Closed;
                    }
                    else if (_time.GetElapsedTime(_openedAt) > _cap)
                    {
                        _timedOut = true;
                        ended = MicrophoneLevelStates.TimedOut;
                    }
                    else if (_time.GetElapsedTime(_leaseFrom) > _lease)
                    {
                        ended = MicrophoneLevelStates.Closed;
                    }
                    else
                    {
                        _status = MicrophoneLevelStates.Listening;
                        _level = level;
                    }
                }

                if (ended is not null)
                {
                    // Leaving the loop disposes the enumerator, which closes the device.
                    break;
                }
            }

            if (ended is null && !ct.IsCancellationRequested)
            {
                // The device stopped by itself: unplugged, or Windows took it away.
                ended = MicrophoneLevelStates.Unavailable;
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
            ended = MicrophoneLevelStates.Unavailable;
        }

        if (ended is not null)
        {
            lock (_state)
            {
                // A listen abandoned earlier and only now letting go speaks for nobody.
                if (generation == _generation)
                {
                    _status = ended;
                    _level = 0;
                }
            }
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
                _retrying = true;
                _retryFrom = _time.GetTimestamp();
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
                await loop.WaitAsync(_closeWait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The driver is slow to let go. It has been told to, and nothing opens until it has.
                _abandoned = loop;
            }
        }

        Release();
        lock (_state)
        {
            ++_generation;
            _status = MicrophoneLevelStates.Closed;
            _level = 0;
            _deviceId = null;
        }

        _askedFor = null;
        _retrying = false;
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
