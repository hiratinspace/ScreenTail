namespace ScreenTail.Shared.Ipc;

/// <summary>
/// Version of the service-to-UI named-pipe contract (Guide §5). Bump it on any breaking message change;
/// ST-004 defines the messages.
///
/// 2 — ST-072 adds <c>reason</c> and <c>scope_process</c> to the capture state, so the HUD can say why
/// capture stopped. Additive, but the framing rejects unknown members on purpose, so an older reader
/// would fail on a newer snapshot rather than ignore the field. The handshake compares versions and
/// refuses a mismatched pair, which is the behaviour that makes this safe to change at all.
/// </summary>
public static class IpcContract
{
    public const int Version = 2;
}
