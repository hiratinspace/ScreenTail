using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScreenTail.Api.Tests.Providers;

/// <summary>
/// A port on this machine that counts who connects to it: what a provider call that escapes to a
/// private address would reach. With an answer set, it reads one request and replies with it; without
/// one, it hangs up at once, which fails a TLS handshake fast instead of waiting out a timeout.
/// </summary>
internal sealed class LoopbackListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _answer;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<int>> _probes = new();
    private int _accepted;

    public LoopbackListener(string? answer = null)
    {
        _answer = answer;
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Accepted => Volatile.Read(ref _accepted);

    /// <summary>
    /// How many had connected before this was called, once every one of them has been counted. It
    /// connects once itself and waits for the listener to take that connection: connections are taken in
    /// the order they arrived, so by then each one made earlier has been taken and counted, however busy
    /// the machine is. The probe is not counted, and is closed without being answered.
    /// </summary>
    public async Task<int> ConnectionsBeforeNowAsync(CancellationToken ct)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var counted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _probes[((IPEndPoint)probe.LocalEndPoint!).Port] = counted;

        await probe.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port), ct);

        // Bounded only so a listener that has stopped fails the test instead of hanging it: a connection
        // on this machine is taken at once.
        return await counted.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (connection.Client.RemoteEndPoint is IPEndPoint from && _probes.TryRemove(from.Port, out var probe))
            {
                connection.Dispose();
                _ = probe.TrySetResult(Accepted);
                continue;
            }

            _ = Interlocked.Increment(ref _accepted);
            using (connection)
            {
                if (_answer is null)
                {
                    continue;
                }

                try
                {
                    var stream = connection.GetStream();
                    var buffer = new byte[4096];
                    var seen = new StringBuilder();
                    while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        _ = seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(_answer), _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // The test is over or the client went away; the count is what matters.
                }
            }
        }
    }
}
