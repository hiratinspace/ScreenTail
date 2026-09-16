using ScreenTail.Core.Ipc;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Shell;

/// <summary>
/// Owns the connection to the capture service on the UI's behalf (ST-085).
///
/// Before this, <see cref="IpcClient"/> had no production caller at all: every window in the UI process
/// rendered a literal snapshot, the tray icon did not exist, and INV-4 — capture is always visibly
/// indicated — was enforced by nothing in the running application (weaknesses P0-2). This is the piece
/// that was missing. It connects, keeps the shell's store fed, notices when the service goes away, keeps
/// trying, and recovers without the technician restarting anything.
///
/// The rule it exists to hold: <b>unknown is not idle.</b> When the pipe drops the session has not
/// stopped, the UI has merely gone blind, so the last state the service reported is kept and the
/// connection is marked unavailable beside it. Everything downstream — the pill, the tray icon, the
/// banner — reads that pairing and says "it may still be recording" rather than "not recording". A UI
/// that reports idle because it lost a pipe is the silent-capture path the whole design forbids.
///
/// Platform-neutral on purpose (ADR-0002). The pipe, the verifier and WPF are all on the other side of an
/// interface, so the reconnect policy can be tested on any machine.
/// </summary>
public sealed class CaptureConnection : IAsyncDisposable
{
    private readonly ShellState _shell;
    private readonly Func<CancellationToken, Task<ICaptureChannel>> _open;
    private readonly Func<int, TimeSpan>? _retryAfter;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _swapping = new(1, 1);
    private ICaptureChannel? _channel;
    private Task? _loop;

    /// <param name="open">Opens one connection, or throws. <see cref="IpcClient.ConnectAsync"/> in production.</param>
    /// <param name="retryAfter">
    /// How long to wait before attempt <c>n</c>. Null never retries, which is what a test wants and
    /// nothing in production does. Defaults to <see cref="RetryAfter"/>.
    /// </param>
    public CaptureConnection(
        ShellState shell,
        Func<CancellationToken, Task<ICaptureChannel>> open,
        Func<int, TimeSpan>? retryAfter = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _retryAfter = retryAfter;
    }

    /// <summary>
    /// Why the service refused us, when it did. Null while things are merely not working yet.
    ///
    /// Distinct from "not connected", and deliberately: a refusal is an answer. A wrong token or a
    /// stranger on the pipe will not fix itself, so the UI stops trying and says which happened, rather
    /// than reopening the pipe every few seconds and writing an audit row each time.
    /// </summary>
    public string? LastRefusal { get; private set; }

    /// <summary>Whether a channel is open right now.</summary>
    public bool IsConnected => _channel?.IsConnected == true;

    /// <summary>
    /// The retry schedule: 250 ms doubling to a ten-second ceiling.
    ///
    /// Quick at the start because the common case is a service that is still starting, and the UI should
    /// catch up within a second rather than making a technician wonder. Capped because the other common
    /// case is a machine where the service is not installed, and a UI that opens a pipe forever is a
    /// background process nobody asked for.
    /// </summary>
    public static TimeSpan RetryAfter(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(10_000, 250L * (1L << Math.Min(Math.Max(attempt, 1) - 1, 20))));

    /// <summary>Starts connecting. Returns as soon as the loop is running, not when it has connected.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("Already started.");
        }

        _shell.Connecting();
        _loop = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends a command, or reports cleanly that there is nobody to send it to.
    ///
    /// Never throws for a missing connection. Every caller is a button, and a button whose handler throws
    /// takes the window with it; "Not connected to the capture service." is something the shell can show.
    /// </summary>
    public async Task<CommandResult> SendAsync(Func<int, IpcCommand> build, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        var channel = _channel;
        if (channel is null || !channel.IsConnected)
        {
            return new CommandResult { RequestId = 0, Ok = false, Error = "Not connected to the capture service." };
        }

        try
        {
            return await channel.SendAsync(build, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or IpcProtocolException)
        {
            // The pipe broke between the check and the write. The reconnect loop has already been told by
            // the channel's own Disconnected; this caller just needs an answer it can display.
            return new CommandResult { RequestId = 0, Ok = false, Error = "The capture service disconnected." };
        }
    }

    /// <summary>
    /// Asks the service something whose answer is an event of its own, or returns null when there is
    /// nobody to ask.
    ///
    /// Null rather than an exception for the same reason <see cref="SendAsync"/> returns a failed result:
    /// every caller is a screen opening, and a screen whose load throws takes the window with it. The
    /// caller shows what it can and says the rest is unknown.
    /// </summary>
    public async Task<TReply?> RequestAsync<TReply>(Func<int, IpcCommand> build, CancellationToken ct = default)
        where TReply : IpcEvent
    {
        ArgumentNullException.ThrowIfNull(build);
        var channel = _channel;
        if (channel is null || !channel.IsConnected)
        {
            return null;
        }

        try
        {
            return await channel.RequestAsync<TReply>(build, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or IpcProtocolException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        await CloseAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _swapping.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connected = false;
            try
            {
                var channel = await _open(ct).ConfigureAwait(false);
                await AttachAsync(channel, dropped).ConfigureAwait(false);
                connected = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IpcRejectedException or IpcUntrustedServerException)
            {
                // An answer, not a failure. Stop: retrying a token the service has refused only writes an
                // audit row each time, and retrying an impostor hands it the token on the next attempt.
                LastRefusal = ex switch
                {
                    IpcRejectedException rejected => rejected.Reason,
                    IpcUntrustedServerException untrusted => untrusted.Reason,
                    _ => RejectReasons.Protocol,
                };
                _shell.Lost();
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or IpcProtocolException or UnauthorizedAccessException)
            {
                // Nothing is listening yet, or the pipe broke mid-handshake. Ordinary while a service is
                // starting, so it is reported as unavailable rather than as an error, and tried again.
                _shell.Lost();
            }

            if (connected)
            {
                try
                {
                    await dropped.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await CloseAsync().ConfigureAwait(false);

                // Said only after the channel is gone, so nothing can report connected in between.
                _shell.Lost();
                if (_retryAfter is null)
                {
                    return;
                }

                // Straight back round without a delay: a service that restarted is usually already up,
                // and the backoff is for repeated failures, not for the first one.
                attempt = 0;
                continue;
            }

            if (_retryAfter is null)
            {
                return;
            }

            try
            {
                await Task.Delay(_retryAfter(attempt), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task AttachAsync(ICaptureChannel channel, TaskCompletionSource dropped)
    {
        await _swapping.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _channel = channel;
            LastRefusal = null;
        }
        finally
        {
            _swapping.Release();
        }

        channel.StateChanged += OnStateChanged;
        channel.Disconnected += () => dropped.TrySetResult();

        // The handshake's state is the first honest paint. Published after subscribing, so a change that
        // lands between the two is not lost.
        _shell.Connected(channel.State);

        if (!channel.IsConnected)
        {
            // It went away between opening and getting here.
            dropped.TrySetResult();
        }
    }

    private void OnStateChanged(CaptureStateSnapshot state) => _shell.Observe(state);

    private async Task CloseAsync()
    {
        await _swapping.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        ICaptureChannel? channel;
        try
        {
            channel = _channel;
            _channel = null;
        }
        finally
        {
            _swapping.Release();
        }

        if (channel is not null)
        {
            channel.StateChanged -= OnStateChanged;
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }
}
