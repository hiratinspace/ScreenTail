using ScreenTail.Core.Speech;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Settings;

/// <summary>What the level meter asks the service. Over the pipe in the running application; a fake in tests.</summary>
public interface IMicrophoneLevelGateway
{
    /// <summary>The level now, which also keeps the service's meter open a moment longer. Null when the service did not answer.</summary>
    Task<MeterReading?> ReadLevelAsync(string? deviceId, CancellationToken ct = default);

    /// <summary>Closes the service's meter now.</summary>
    Task StopLevelAsync(CancellationToken ct = default);
}

/// <summary>
/// Settings, Capture's live level meter, on the UI's side (ST-080 AC3).
///
/// The card says when it is being looked at (on screen, the window in front and not minimised, the
/// desktop unlocked and awake) and this asks the service for the level about ten times a second for
/// exactly as long as that is true. Each request waits for the one before, so the pipe never holds more
/// than one, and each waits first for the UI thread to answer (<c>responsive</c>), so a window that has
/// hung stops asking and the service closes the microphone by itself. When the card stops being looked
/// at, the asking stops first and the service is then told to close the microphone; if that message is
/// lost or slow, the service closes it anyway once the requests stop (<see cref="MicrophoneMeter"/>).
/// A reading that arrives after the card was hidden is not shown.
/// </summary>
public sealed class MicrophoneLevelWatch : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long putting the card away waits for the service to say it closed the microphone.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    public const string WhileWatching = "Live while this card is on screen. Only the level is read: nothing is recorded, kept or sent.";

    public const string NotWatching = "The level shows while this card is on screen and the window is in front.";

    private readonly IMicrophoneLevelGateway _gateway;
    private readonly Func<CancellationToken, Task> _wait;
    private readonly Func<CancellationToken, Task>? _responsive;
    private readonly SemaphoreSlim _switching = new(1, 1);
    private CancellationTokenSource? _watching;
    private Task? _loop;
    private volatile string? _deviceId;

    /// <param name="wait">The pause between two requests; <see cref="Interval"/> when null. A test passes its own.</param>
    /// <param name="responsive">
    /// Completes when the UI thread has answered: a round trip through its dispatcher. Each request waits
    /// for it, however long, so a hung window asks for nothing. Null asks without checking (tests).
    /// </param>
    public MicrophoneLevelWatch(IMicrophoneLevelGateway gateway, Func<CancellationToken, Task>? wait = null, Func<CancellationToken, Task>? responsive = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _wait = wait ?? (ct => Task.Delay(Interval, ct));
        _responsive = responsive;
    }

    /// <summary>Whether the card is being looked at, and so the level asked for.</summary>
    public bool Watching => _loop is not null;

    /// <summary>The device the picker shows, saved or not; null is Windows' default. The next request asks for it.</summary>
    public string? DeviceId
    {
        get => _deviceId;
        set => _deviceId = value;
    }

    /// <summary>0 to 1. Zero whenever the card is not being looked at.</summary>
    public double Level { get; private set; }

    /// <summary>One sentence for under the meter.</summary>
    public string Status { get; private set; } = NotWatching;

    /// <summary>The level or the sentence changed. Raised on whichever thread the reading arrived on.</summary>
    public event Action? Changed;

    /// <summary>The card became looked at, or stopped being. Calls in a row with the same value do nothing.</summary>
    public async Task SetWatchingAsync(bool watching)
    {
        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            if (watching == Watching)
            {
                return;
            }

            if (watching)
            {
                _watching = new CancellationTokenSource();
                var token = _watching.Token;
                _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
                return;
            }

            // The asking ends before the service is told, so the close is the last thing it hears. The
            // loop never throws (RunAsync catches everything), so it is always put away here.
            try
            {
                await _watching!.CancelAsync().ConfigureAwait(false);
                await _loop!.ConfigureAwait(false);
            }
            finally
            {
                _watching?.Dispose();
                _watching = null;
                _loop = null;
                Show(0, NotWatching);
            }

            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _switching.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await SetWatchingAsync(false).ConfigureAwait(false);
        _switching.Dispose();
    }

    /// <summary>The sentence under the meter for a reading, or for no answer at all.</summary>
    public static string Describe(MeterReading? reading) => reading?.State switch
    {
        null => "The capture service did not answer, so the level cannot be shown.",
        MicrophoneLevelStates.Listening => WhileWatching,
        MicrophoneLevelStates.Opening or MicrophoneLevelStates.Closed => "Opening the microphone.",
        MicrophoneLevelStates.Session => "A session is using the microphone. The level shows between sessions.",
        MicrophoneLevelStates.NoMicrophone => "No microphone is connected.",
        MicrophoneLevelStates.TimedOut => "The level stopped after five minutes. Switch to another window and back to see it again.",
        _ => "The microphone could not be opened. Windows may be blocking it in its privacy settings.",
    };

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_responsive is not null)
                {
                    // Nothing is asked for while the window cannot answer: the lease then runs out on
                    // the service's side and the microphone closes.
                    await _responsive(ct).WaitAsync(ct).ConfigureAwait(false);
                }

                MeterReading? reading;
                try
                {
                    reading = await _gateway.ReadLevelAsync(_deviceId, ct).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // One failed request is shown as no answer and the next one tries again.
                catch (Exception) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    reading = null;
                }

                if (ct.IsCancellationRequested)
                {
                    // Hidden while the request was out: what it says is not shown.
                    return;
                }

                Show(reading is { State: MicrophoneLevelStates.Listening } ? Math.Clamp(reading.Level, 0, 1) : 0, Describe(reading));
                await _wait(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Hidden.
        }
#pragma warning disable CA1031 // A meter that fails stops asking and says so; it must not wedge the card.
        catch (Exception)
#pragma warning restore CA1031
        {
            if (!ct.IsCancellationRequested)
            {
                Show(0, Describe(null));
            }
        }
    }

    private async Task StopAsync()
    {
        // Bounded: a service that does not answer cannot hold the card. Its lease closes the device anyway.
        using var giveUp = new CancellationTokenSource(StopWait);
        try
        {
            await _gateway.StopLevelAsync(giveUp.Token).WaitAsync(StopWait).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The stop is a courtesy on top of the lease; no failure of it may reach the window.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Timed out, cancelled or failed: the lease runs out on the service's side within seconds.
        }
    }

    private void Show(double level, string status)
    {
        Level = level;
        Status = status;
        Changed?.Invoke();
    }
}
