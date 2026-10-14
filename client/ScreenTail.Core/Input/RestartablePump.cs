namespace ScreenTail.Core.Input;

/// <summary>
/// One run of a <see cref="RestartablePump"/>: its own stop signal, and the moment it says it is ready.
/// </summary>
public sealed class PumpRun : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal PumpRun() => Stopping = _stopping.Token;

    /// <summary>
    /// Set when this run is asked to stop. A loop that blocks registers its wake-up here; a registration
    /// made after the stop runs at once, so a stop is never missed.
    /// </summary>
    public CancellationToken Stopping { get; }

    /// <summary>Says the run is in and its loop is about to begin. Until this, a start has not succeeded.</summary>
    public void Ready() => _ready.TrySetResult();

    internal Task ReadyTask => _ready.Task;

    internal Task Exited => _exited.Task;

    internal bool HasExited => _exited.Task.IsCompleted;

    internal bool StopRequested { get; private set; }

    internal void RequestStop()
    {
        StopRequested = true;
        _stopping.Cancel();
    }

    internal void Execute(Action<PumpRun> body)
    {
        Exception? failure = null;
        try
        {
            body(this);
        }
        catch (Exception ex)
        {
            // Nothing may escape a thread: it would end the service. A failure before Ready fails the start
            // with it; one after ends this run, and the next start begins a new one.
            failure = ex;
        }
        finally
        {
            // Exited first, so whoever sees the start fail also sees the run gone and starts afresh.
            _ = _exited.TrySetResult();
            _ = _ready.TrySetException(failure ?? new InvalidOperationException("The pump ended before it was ready."));
        }
    }

    public void Dispose() => _stopping.Dispose();
}

/// <summary>
/// A dedicated thread that runs a loop, and can be stopped and started again for as long as the process
/// lives (ST-024, T-33).
///
/// The input hooks follow the session (ST-031), so one hooks object is started and stopped once per session
/// for the whole life of the service. Each start is a new run with a stop signal of its own: a signal kept
/// from the last session would end the new loop before it began. A start succeeds only once the run says it
/// is ready, so a loop that ended first is reported as a failure and never as installed.
///
/// One run at a time. Two loops at once would share whatever the body keeps in its object (the hook
/// handles), so a start that meets a run still stopping refuses rather than starting another beside it.
/// Nothing here is on a hot path: it runs once per session start and stop.
/// </summary>
/// <param name="name">The thread's name, for a debugger or a dump.</param>
/// <param name="body">The loop. Calls <see cref="PumpRun.Ready"/> once it is in, then runs until
/// <see cref="PumpRun.Stopping"/> is set.</param>
/// <param name="prepare">Configures each new thread before it starts (the Windows host makes it STA).</param>
/// <param name="stopWait">How long a stop waits for the loop to end. Five seconds unless a test says otherwise.</param>
public sealed class RestartablePump(
    string name,
    Action<PumpRun> body,
    Action<Thread>? prepare = null,
    TimeSpan? stopWait = null) : IAsyncDisposable
{
    private readonly TimeSpan _stopWait = stopWait ?? TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private PumpRun? _current;
    private bool _disposed;

    /// <summary>Whether a run's thread is alive, including one that has been asked to stop and has not yet.</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _current is { HasExited: false };
            }
        }
    }

    /// <summary>
    /// Starts a run and waits until it is ready, or joins the run already going.
    /// </summary>
    /// <exception cref="InvalidOperationException">The last run was asked to stop and has not ended, or the
    /// loop ended before it was ready.</exception>
    /// <exception cref="ObjectDisposedException">The pump has been disposed.</exception>
    public async Task StartAsync(CancellationToken ct = default)
    {
        PumpRun run;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is { HasExited: false } live)
            {
                if (live.StopRequested)
                {
                    throw new InvalidOperationException("The last run has not ended yet.");
                }

                run = live;
            }
            else
            {
                // A run that ended on its own (a message loop that failed) is replaced, never reported as
                // still going.
                _current?.Dispose();
                run = new PumpRun();
                var thread = new Thread(() => run.Execute(body)) { IsBackground = true, Name = name };
                prepare?.Invoke(thread);
                _current = run;
                thread.Start();
            }
        }

        try
        {
            await run.ReadyTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up and will not remove what it does not know went in, so it does not stay in.
            // Nobody awaits this run's ready signal now, and a loop that ends before it is ready faults it.
            _ = run.ReadyTask.ContinueWith(
                static ready => _ = ready.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            try
            {
                await StopRunAsync(run, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still stopping. The next start refuses until it has ended.
            }

            throw;
        }
    }

    /// <summary>Asks the current run to stop and waits for its loop to end.</summary>
    /// <exception cref="TimeoutException">The loop did not end in time. It has been asked to, and the next
    /// start refuses until it has.</exception>
    public async Task StopAsync(CancellationToken ct = default)
    {
        PumpRun? run;
        lock (_gate)
        {
            run = _current;
        }

        if (run is not null)
        {
            await StopRunAsync(run, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A background thread that will not end does not hold the process open, and a dispose that
            // throws would take the rest of shutdown with it.
        }
    }

    private async Task StopRunAsync(PumpRun run, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!run.StopRequested && !run.HasExited)
            {
                // Under the gate, so a start never joins a run that is stopping and a run is never disposed
                // under a stop. The wake the loop registered runs here, so it must be quick.
                run.RequestStop();
            }
        }

        await run.Exited.WaitAsync(_stopWait, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (ReferenceEquals(_current, run))
            {
                _current = null;
                run.Dispose();
            }
        }
    }
}
