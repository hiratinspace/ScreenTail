using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Ipc;

/// <summary>
/// What the machine tells every connected UI, as <c>state_changed</c>: every transition, and every count
/// that moved without one (ST-072). Here rather than inline in the host, so the wiring is tested: the
/// count's half was added to the host on its own and nothing failed when it was taken out again
/// (2026-11-05 review).
/// </summary>
public static class StateBroadcast
{
    /// <param name="machine">The session machine whose snapshots are told.</param>
    /// <param name="broadcast">Sends one event to every connected client; the IPC server's broadcast in the host.</param>
    public static void Wire(SessionMachine machine, Func<IpcEvent, Task> broadcast)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(broadcast);
        machine.StateChanged += snapshot => _ = broadcast(new StateChanged { State = snapshot });

        // The redaction backlog moving is said as well, as the drafts count already is, so the pill's
        // "redacting" count follows the worker rather than standing at its value from the last state
        // change (owner's report, 2026-11-05). The same message and shape: no contract change.
        machine.CountsChanged += snapshot => _ = broadcast(new StateChanged { State = snapshot });
    }
}
