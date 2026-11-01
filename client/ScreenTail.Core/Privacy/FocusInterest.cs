using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Privacy;

/// <summary>
/// Whether the password-field guard has any use for hearing that keyboard focus moved, and a say in it
/// whenever that changes (ST-031, ST-040).
///
/// The focus hook is one of the few things the capture service keeps with no session: Windows raises
/// <c>EVENT_OBJECT_FOCUS</c> for every control in every application, and each one woke the hook's thread
/// and then the guard, which looked at the session, saw none and went back to sleep. The guard decides
/// nothing between sessions (<see cref="PasswordFieldGuard.TickAsync"/>), so the hook is wanted exactly when
/// a pass could decide something: a session under way (paused included, so a hold can be taken or let go
/// before Resume) or a hold still in place.
///
/// A session starting does not wait on the hook. The guard wakes on the same transition and asks what has
/// focus there and then, so a field already focused is held at once; the hook is back in a moment after,
/// and the guard's one-second backstop covers a focus move in between.
/// </summary>
public sealed class FocusInterest : IDisposable
{
    private readonly SessionMachine _machine;
    private readonly Action<bool> _listen;
    private readonly Lock _gate = new();
    private bool _wanted;
    private bool _disposed;

    /// <param name="machine">The session whose life the hook follows.</param>
    /// <param name="listen">
    /// Told true when focus events are wanted and false when they are not: told once now, then on every
    /// change and never twice in a row with the same answer. Called on the thread that changed the state,
    /// so it must only hand the answer on (the Windows watcher posts it to its own thread).
    /// </param>
    public FocusInterest(SessionMachine machine, Action<bool> listen)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(listen);
        _machine = machine;
        _listen = listen;
        lock (_gate)
        {
            _wanted = Wanted(machine);
            _listen(_wanted);
        }

        machine.StateChanged += OnStateChanged;
    }

    /// <summary>Whether focus events are wanted right now.</summary>
    public bool Listening
    {
        get
        {
            lock (_gate)
            {
                return _wanted;
            }
        }
    }

    /// <summary>
    /// The rule itself: a session under way or a password-field hold still kept. Everything else is an idle
    /// service, where the guard asks nothing and so has nothing to hear.
    /// </summary>
    public static bool Wanted(SessionMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return SessionLoop.IsUnderWay(machine.State) || machine.IsHeld(CaptureStateReason.PasswordField);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _machine.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(CaptureStateSnapshot snapshot)
    {
        lock (_gate)
        {
            // Read from the session rather than the snapshot: a hold is not in the snapshot, and the latest
            // answer is the one to act on whichever transition this listener was told of.
            var wanted = Wanted(_machine);
            if (_disposed || wanted == _wanted)
            {
                return;
            }

            _wanted = wanted;
            _listen(wanted);
        }
    }
}
