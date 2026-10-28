using ScreenTail.Core.Privacy;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Sessions;

/// <summary>
/// A loop that ticks while a session is under way and waits on the state machine otherwise (ST-031).
///
/// The click drain, the scene sampler and the sensitive-context guard each ran a timer for the life of the
/// service: twenty, one and four times a second, every second of the day, to find that there was no
/// session and nothing to do. A technician records for a fraction of the day, and each of those ticks is a
/// core leaving a low-power state on a laptop that may be on battery. The state machine already says when
/// a session starts and ends, so the timer exists only between the two.
///
/// <b>Three rules keep the loops doing what they did.</b> The step runs at once when a session begins,
/// rather than a period later, so a session that starts over something a guard should act on is acted on
/// at its first turn, as the session starts. It runs on the period for as long as the session is under way, in every state a
/// step has something to do in: recording, paused, suppressed and finalizing, the states in which the
/// input hooks are in. And it runs once more after the session has ended, so a step that tidies up on
/// seeing a session over (the scene sampler forgetting its baseline, the drain emptying what the hooks
/// left) still sees it.
/// </summary>
public static class SessionLoop
{
    /// <summary>
    /// Whether a session is under way: the input hooks are in, frames may be taken or are still being
    /// read, and a guard may have a hold to keep or let go of.
    /// </summary>
    public static bool IsUnderWay(SessionState state) =>
        state is SessionState.Recording or SessionState.Paused or SessionState.Suppressed or SessionState.Finalizing;

    /// <param name="machine">The session whose life the loop follows.</param>
    /// <param name="period">How often the step runs while a session is under way.</param>
    /// <param name="step">One turn. Runs at a session's start, on the period, and once after its end.</param>
    /// <param name="onFailure">Told when a turn throws; the loop carries on (<see cref="ResilientLoop"/>).</param>
    /// <param name="time">The clock the period is measured on. The system's when null.</param>
    public static async Task RunAsync(
        SessionMachine machine,
        TimeSpan period,
        Func<CancellationToken, Task> step,
        Action<Exception>? onFailure,
        CancellationToken ct,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(step);
        using var changes = new StateChanges(machine);
        try
        {
            while (true)
            {
                await changes.UntilAsync(IsUnderWay, ct).ConfigureAwait(false);

                using (var timer = new PeriodicTimer(period, time ?? TimeProvider.System))
                {
                    var first = true;
                    await ResilientLoop.RunAsync(
                        next: async token =>
                        {
                            if (first)
                            {
                                first = false;
                                return true;
                            }

                            return IsUnderWay(machine.State) && await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
                        },
                        step,
                        onFailure,
                        ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();

                // The session is over and the timer gone. One more turn, which sees it over.
                var last = true;
                await ResilientLoop.RunAsync(
                    next: _ =>
                    {
                        var go = last;
                        last = false;
                        return Task.FromResult(go);
                    },
                    step,
                    onFailure,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Wakes a waiter when the machine changes state, without losing a change that lands between the
    /// waiter looking at the state and starting to wait: the signal it waits on is taken before it looks.
    /// </summary>
    private sealed class StateChanges : IDisposable
    {
        private readonly SessionMachine _machine;
        private TaskCompletionSource _next = Fresh();

        public StateChanges(SessionMachine machine)
        {
            _machine = machine;
            _machine.StateChanged += OnChanged;
        }

        public async Task UntilAsync(Func<SessionState, bool> wanted, CancellationToken ct)
        {
            while (true)
            {
                var next = Volatile.Read(ref _next);
                if (wanted(_machine.State))
                {
                    return;
                }

                await next.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        public void Dispose() => _machine.StateChanged -= OnChanged;

        private void OnChanged(CaptureStateSnapshot snapshot) => Interlocked.Exchange(ref _next, Fresh()).TrySetResult();

        private static TaskCompletionSource Fresh() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
