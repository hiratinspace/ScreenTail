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
    private int _accepted;

    public LoopbackListener(string? answer = null)
    {
        _answer = answer;
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int Accepted => Volatile.Read(ref _accepted);

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
