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
/// The card says when it is being looked at (on screen, the window in front and not minimised) and this
/// asks the service for the level about ten times a second for exactly as long as that is true. Each
/// request waits for the one before, so the pipe never holds more than one. When the card stops being
/// looked at, the asking stops first and the service is then told to close the microphone; if that
/// message is lost, the service closes it anyway once the requests stop (<see cref="MicrophoneMeter"/>).
/// A reading that arrives after the card was hidden is not shown.
/// </summary>
public sealed class MicrophoneLevelWatch : IAsyncDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    public const string WhileWatching = "Live while this card is on screen. Only the level is read: nothing is recorded, kept or sent.";

    public const string NotWatching = "The level shows while this card is on screen and the window is in front.";

    private readonly IMicrophoneLevelGateway _gateway;
    private readonly Func<CancellationToken, Task> _wait;
    private readonly SemaphoreSlim _switching = new(1, 1);
    private CancellationTokenSource? _watching;
    private Task? _loop;
    private volatile string? _deviceId;

    /// <param name="wait">The pause between two requests; <see cref="Interval"/> when null. A test passes its own.</param>
    public MicrophoneLevelWatch(IMicrophoneLevelGateway gateway, Func<CancellationToken, Task>? wait = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _wait = wait ?? (ct => Task.Delay(Interval, ct));
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

            // The asking ends before the service is told, so the close is the last thing it hears.
            await _watching!.CancelAsync().ConfigureAwait(false);
            await _loop!.ConfigureAwait(false);
            _watching.Dispose();
            _watching = null;
            _loop = null;
            Show(0, NotWatching);
            await _gateway.StopLevelAsync().ConfigureAwait(false);
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
        _ => "The microphone could not be opened. Windows may be blocking it in its privacy settings.",
    };

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var reading = await _gateway.ReadLevelAsync(_deviceId, ct).ConfigureAwait(false);
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
    }

    private void Show(double level, string status)
    {
        Level = level;
        Status = status;
        Changed?.Invoke();
    }
}
