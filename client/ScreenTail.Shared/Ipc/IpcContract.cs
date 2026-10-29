namespace ScreenTail.Shared.Ipc;

/// <summary>
/// Version of the service-to-UI named-pipe contract (Guide §5). Bump it on any breaking message change;
/// ST-004 defines the messages.
///
/// 2 — ST-072 adds <c>reason</c> and <c>scope_process</c> to the capture state, so the HUD can say why
/// capture stopped. Additive, but the framing rejects unknown members on purpose, so an older reader
/// would fail on a newer snapshot rather than ignore the field. The handshake compares versions and
/// refuses a mismatched pair, which is the behaviour that makes this safe to change at all.
///
/// 3: ST-081 adds <c>effective_local_only</c> and <c>effective_retention_days</c> to <c>settings</c>, so
/// Privacy shows a locked field as the admin set it rather than the technician's own value. Same reason
/// as 2: an older reader would fail on the new members.
///
/// 4: the <c>audit</c> reply carries where the export was written and its verdict instead of the whole
/// log as one string, which outgrew the frame limit within weeks (2026-10-12 review, E-2). The reply
/// changes shape, so an older reader would fail on it.
///
/// 5: ST-013 adds the <c>draft_pending</c> state (a session over, its draft on its way to the backend) and
/// <c>draft_pending</c> on the <c>session</c> reply, so Review says the note is being drafted rather than
/// that drafting failed. An older UI would show the new state as "unknown, may be recording".
/// </summary>
public static class IpcContract
{
    public const int Version = 5;
}
