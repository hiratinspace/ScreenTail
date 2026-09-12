namespace ScreenTail.Service.Host;

/// <summary>Exactly one capture service per user (ADR-0003): a named mutex scoped to this session.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <returns>The held instance, or null when another service for this user is already running.</returns>
    public static SingleInstance? TryAcquire(string userIdentity)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\ScreenTail.Service." + Core.Ipc.IpcPipeNames.ForUser(userIdentity), out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
