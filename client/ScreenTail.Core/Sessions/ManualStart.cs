using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Sessions;

/// <summary>
/// A start sent over the pipe (ST-020). Its only sender is the onboarding wizard's test session (ST-083):
/// the tray has no start item, and Ctrl+Alt+R is a hotkey the service registers itself and starts through
/// the coordinator. It keeps the same two rules as the sessions the service starts.
///
/// <b>Something on screen says capture is on (INV-4).</b> The same indicator reports the guard and the
/// automatic start read; with none current nothing starts. A zero-size report, the pill put away between
/// sessions, counts (owner decision, 2026-10-25).
///
/// <b>The session starts under the tenant's policy as it stands now (ST-047).</b> The same local-only flag
/// and policy version the host hands the automatic start, so every session records the policy it ran
/// under, whichever way it was started (INV-8).
///
/// The session is attributed to <c>other</c>: the pipe names no tool, and what is in front when the
/// wizard is the window is the wizard. In Core, so the Mac suite holds all of it.
/// </summary>
/// <param name="indicated">Whether an indicator report is current.</param>
/// <param name="sessionPolicy">Local-only as it is in force now, and the policy's version. Required.</param>
public sealed class ManualStart(SessionMachine machine, Func<bool> indicated, Func<(bool LocalOnly, string? PolicyVersion)> sessionPolicy)
{
    /// <summary>
    /// Why nothing started, in words the technician reads under the wizard's Start button: what is
    /// missing, why it matters, and what to do.
    /// </summary>
    public const string NotIndicated =
        "The recording pill is not on screen, so the session was not started. ScreenTail never records without "
        + "showing it. Wait a few seconds for the pill to appear, then try again; if it does not, quit "
        + "ScreenTail from the tray and open it again.";

    /// <summary>Why nothing started while Windows Installer is replacing ScreenTail (ST-113).</summary>
    public const string Updating = "ScreenTail is installing an update, so no session can start. It starts again once the update has finished.";

    private readonly SessionMachine _machine = machine ?? throw new ArgumentNullException(nameof(machine));
    private readonly Func<bool> _indicated = indicated ?? throw new ArgumentNullException(nameof(indicated));
    private readonly Func<(bool LocalOnly, string? PolicyVersion)> _sessionPolicy = sessionPolicy ?? throw new ArgumentNullException(nameof(sessionPolicy));

    public async Task<CommandResult> StartAsync(StartCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_machine.StartsHeld)
        {
            return new CommandResult { RequestId = command.RequestId, Ok = false, Error = Updating };
        }

        if (!_indicated())
        {
            return new CommandResult { RequestId = command.RequestId, Ok = false, Error = NotIndicated };
        }

        // Read as the session starts, not when the service did: a policy fetched since applies.
        var (localOnly, policyVersion) = _sessionPolicy();
        var started = await _machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Other }, localOnly, policyVersion, ct: ct).ConfigureAwait(false);
        return new CommandResult
        {
            RequestId = command.RequestId,
            Ok = started,
            Error = started ? null : _machine.StartsHeld ? Updating : $"Not possible while {_machine.State.ToWire()}.",
        };
    }
}
