using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>The views the shell navigates between (Spec §5 S3, S4, S5–S7).</summary>
public enum ShellView
{
    Review,
    History,
    Settings,
}

/// <summary>How the shell stands in relation to the capture service.</summary>
public enum ServiceConnection
{
    /// <summary>Trying, or about to. The UI shows nothing alarming yet — a service starting is normal.</summary>
    Connecting,

    Connected,

    /// <summary>
    /// The service is not answering. Spec §5 S1 shows a banner offering to start it, because a technician
    /// whose UI silently shows an old state will not find out until a session was never recorded.
    /// </summary>
    Unavailable,
}

/// <param name="Banner">What to show across the top, or null when there is nothing wrong.</param>
public sealed record ShellSnapshot(
    ServiceConnection Connection,
    CaptureStateSnapshot? Capture,
    ShellView View,
    string? Banner);

/// <summary>
/// What every view in the UI reads from (ST-070).
///
/// One store rather than each window holding its own copy. The alternative — Review, History and the HUD
/// each listening to the pipe — gives three views that disagree after a dropped event, and a technician
/// who sees "recording" in one window and "idle" in another has no way to know which is true.
///
/// It holds no capture state of its own: the service owns that (ADR-0003), and this is the last thing the
/// service said. That is why <see cref="Capture"/> keeps its last value when the connection drops rather
/// than resetting — the session did not stop because the UI lost the pipe, and showing "idle" would be a
/// lie about someone's recording.
///
/// Platform-neutral so it can be argued about without WPF and without a Windows machine. The shell binds
/// to it; it binds to nothing.
/// </summary>
public sealed class ShellState
{
    private readonly Lock _gate = new();
    private ServiceConnection _connection = ServiceConnection.Connecting;
    private CaptureStateSnapshot? _capture;
    private ShellView _view = ShellView.Review;

    /// <summary>Raised after every change, with everything a view needs. Handlers must not block.</summary>
    public event Action<ShellSnapshot>? Changed;

    public ShellSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return Build();
            }
        }
    }

    /// <summary>The last thing the service said, whether or not it is still connected.</summary>
    public CaptureStateSnapshot? Capture
    {
        get
        {
            lock (_gate)
            {
                return _capture;
            }
        }
    }

    public void Connecting() => Set(() => _connection = ServiceConnection.Connecting);

    /// <param name="state">The state the handshake returned, so the first paint is the real one.</param>
    public void Connected(CaptureStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Set(() =>
        {
            _connection = ServiceConnection.Connected;
            _capture = state;
        });
    }

    public void Observe(CaptureStateSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Set(() =>
        {
            // An event arriving while the UI thought it was disconnected means it was wrong, and the
            // service is the authority. Reconnecting on evidence beats waiting for a retry timer.
            _connection = ServiceConnection.Connected;
            _capture = state;
        });
    }

    /// <summary>The pipe dropped, or the service never answered. What it last said is kept.</summary>
    public void Lost() => Set(() => _connection = ServiceConnection.Unavailable);

    public void Navigate(ShellView view) => Set(() => _view = view);

    private void Set(Action change)
    {
        ShellSnapshot snapshot;
        lock (_gate)
        {
            change();
            snapshot = Build();
        }

        // Raised outside the lock: a handler that navigates, or that reads Snapshot, would otherwise
        // deadlock against a non-reentrant lock, and it would do it only under a race.
        Changed?.Invoke(snapshot);
    }

    private ShellSnapshot Build() => new(_connection, _capture, _view, BannerFor(_connection));

    /// <summary>
    /// Spec §5 S1's wording, and no banner at all when nothing is wrong: a bar that is always there is a
    /// bar nobody reads, including on the day it says something that matters.
    /// </summary>
    private static string? BannerFor(ServiceConnection connection) => connection switch
    {
        ServiceConnection.Unavailable => "Capture service not running — Start",
        _ => null,
    };
}
