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
/// ST-113's <c>get_update</c>, <c>install_update</c>, <c>set_update_channel</c> and the <c>update</c>
/// reply are new commands, and <c>update</c> is only ever sent in reply to <c>get_update</c>, so an older
/// UI never receives it: not a breaking change, and the version is not bumped (as with
/// <c>get_frame_thumbnail</c>).
/// </summary>
public static class IpcContract
{
    public const int Version = 4;
}
