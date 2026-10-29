using ScreenTail.Core.Outbox;

namespace ScreenTail.Core.Sessions;

/// <summary>
/// How the capture service tells the session machine that the outbox settled a draft (ST-013). In Core,
/// so the Mac suite holds the wiring: <c>CaptureHost</c> sets <see cref="Outbox.Outbox.Settled"/> to
/// <see cref="Listener"/> and does nothing else with it.
/// </summary>
public static class DraftSettling
{
    /// <summary>
    /// A draft item done or failed for good goes to <see cref="SessionMachine.DraftSettledAsync"/>, with
    /// the outbox's last error as the reason. Every other kind of item is not the machine's business.
    /// </summary>
    public static Func<OutboxItem, CancellationToken, Task> Listener(SessionMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return async (item, ct) =>
        {
            if (item.Kind == OutboxKind.Draft)
            {
                _ = await machine.DraftSettledAsync(item.SessionId, item.State == OutboxState.Done, item.LastError, ct).ConfigureAwait(false);
            }
        };
    }
}
