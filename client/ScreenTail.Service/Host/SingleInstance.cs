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
        // Not owned. What makes this work is the name existing while the handle is open, and ownership
        // brought a rule with it that this code could not keep: a mutex may only be released by the
        // thread that took it, and Dispose runs after an await, which resumes wherever it likes. So
        // every clean shutdown ended in an ApplicationException and a non-zero exit code — a service
        // that always crashed on the way out, and a log that always said so (2026-09-19 review).
        var mutex = new Mutex(initiallyOwned: false, @"Local\ScreenTail.Service." + Core.Ipc.IpcPipeNames.ForUser(userIdentity), out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}
