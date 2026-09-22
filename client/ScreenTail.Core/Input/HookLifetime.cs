using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Input;

/// <summary>
/// Keeps the input hooks installed exactly while there is a session (ST-024, ST-031).
///
/// A <c>WH_MOUSE_LL</c> hook makes every mouse movement on the machine switch into this process
/// synchronously, and they were installed for the life of the service. A technician records for a
/// fraction of their day; for the rest of it every mouse move on the desktop paid a context switch into
/// a process that was going to throw the result away. That cost does not show up as ScreenTail's CPU —
/// it shows up as input latency in whatever the technician is actually doing (2026-09-20 review).
///
/// <b>The session, not the recording state.</b> Following suppression would be the obvious reading of
/// "only while recording" and it is the wrong one: a password field takes focus many times in a
/// session, and removing and reinstalling a global hook on each one is churn bought for nothing,
/// because the machine already drops what the hooks see while suppressed (INV-6). What is worth acting
/// on is that there is no session at all for most of the day.
///
/// <b>Nothing is lost by starting late.</b> A session begins from a foreground change or a hotkey —
/// neither of which is a click these hooks would have seen — and the recorder already drops the click
/// that starts one as pre-session.
/// </summary>
/// <param name="install">Installs the hooks. Throwing means the machine would not give us one.</param>
/// <param name="remove">Removes them. Expected to succeed; a failure is counted like any other.</param>
public sealed class HookLifetime(
    SessionMachine machine,
    Func<CancellationToken, Task> install,
    Func<CancellationToken, Task> remove) : IDisposable
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private bool _installed;
    private Action? _stopWatching;

    /// <summary>Whether the hooks are installed, as far as this knows.</summary>
    public bool Installed
    {
        get
        {
            lock (_one)
            {
                return _installed;
            }
        }
    }

    /// <summary>
    /// How many times installing or removing threw.
    ///
    /// ST-021's rule: a machine that will not give us a hook is reported rather than hidden, and the
    /// session carries on recording what it can still see. A count, never a message (INV-10).
    /// </summary>
    public int Failures { get; private set; }

    /// <summary>Raised when installing or removing failed, so the host can log the kind.</summary>
    public event Action<Exception>? Failed;

    /// <summary>
    /// Raised when the hooks went in or came out.
    ///
    /// The log used to say at startup whether the hooks installed, which is the first thing anybody
    /// asks when nothing is being recorded. Installing lazily would have taken that away, so it is said
    /// at the first moment it is true instead.
    /// </summary>
    public event Action<bool>? Changed;

    /// <summary>
    /// Brings the hooks into line with the session.
    ///
    /// One at a time: transitions can arrive close together — start then suppress — and two of these
    /// overlapping would race to install and remove the same global hook.
    /// </summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        var wanted = machine.State is SessionState.Recording
            or SessionState.Paused
            or SessionState.Suppressed
            or SessionState.Finalizing;

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (wanted == _installed)
            {
                return;
            }

            if (wanted)
            {
                await install(ct).ConfigureAwait(false);
            }
            else
            {
                await remove(ct).ConfigureAwait(false);
            }

            _installed = wanted;
            Changed?.Invoke(wanted);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Not a reason to lose the session. A machine that refuses the hook records fewer kinds of
            // event and still records the rest, which is what ST-021 asks for; retrying on the next
            // transition costs nothing and may work.
            Failures++;
            Failed?.Invoke(failure);
        }
        finally
        {
            _ = _one.Release();
        }
    }

    /// <summary>
    /// Follows the session from here on.
    ///
    /// The transition is the event. Polling would mean a session that starts between two ticks records
    /// its first seconds with no hooks, which is the one thing this must not do.
    /// </summary>
    public void Follow(CancellationToken ct)
    {
        void OnChanged(CaptureStateSnapshot snapshot) => _ = ApplyAsync(ct);

        machine.StateChanged += OnChanged;
        _stopWatching = () => machine.StateChanged -= OnChanged;
    }

    public void Dispose()
    {
        _stopWatching?.Invoke();
        _one.Dispose();
    }
}
