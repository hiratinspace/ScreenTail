namespace ScreenTail.Shared.Ipc;

/// <summary>
/// Version of the service-to-UI named-pipe contract (Guide §5). Bump it on any breaking message change;
/// ST-004 defines the messages.
/// </summary>
public static class IpcContract
{
    public const int Version = 1;
}
