using System.IO.Pipes;
using ScreenTail.Core.Ipc;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Ipc;

/// <summary>
/// ST-070. Closing the shell window now cancels the loads it started, so a request can be cancelled
/// while its frame is still going out. The pipe is shared by everything the UI asks, and a frame cut
/// off partway would leave the service reading the rest of the conversation as the remainder of it.
/// The service plays its part by hand here, so the test can stop reading and fill the pipe.
/// </summary>
public sealed class IpcClientCancellationTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    // Far more than a pipe or socket buffers, so the write is still under way when the token fires.
    private static readonly string LargeQuery = new('q', 512 * 1024);

    [Fact]
    public async Task ARequestCancelledWhileItsFrameIsGoingOutStillLeavesWholeFrames()
    {
        var (server, client) = await ConnectByHandAsync();
        await using var service = server;
        await using var ui = client;

        using var closing = new CancellationTokenSource();
        var cancelled = ui.SendAsync(id => new SearchTicketsCommand { RequestId = id, Query = LargeQuery }, closing.Token);
        await Task.Delay(200);
        Assert.False(cancelled.IsCompleted, "the service is not reading, so the large frame should still be going out");
        await closing.CancelAsync();

        // The window closed; something else asks next, and only then does the service read again.
        var next = ui.SendAsync(id => new GetStateCommand { RequestId = id });

        var first = await ReadFrameAsync(service);
        Assert.Equal(LargeQuery, Assert.IsType<SearchTicketsCommand>(first).Query);
        var second = Assert.IsType<GetStateCommand>(await ReadFrameAsync(service));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Soon));
        await IpcFraming.WriteAsync<IpcEvent>(service, new CommandResult { RequestId = second.RequestId, Ok = true });
        Assert.True((await next.WaitAsync(Soon)).Ok);
        Assert.True(ui.IsConnected);
    }

    [Fact]
    public async Task ARequestCancelledBeforeItsTurnOnThePipeIsNeverSent()
    {
        var (server, client) = await ConnectByHandAsync();
        await using var service = server;
        await using var ui = client;

        // One request holds the pipe; a second waits its turn and is cancelled while it waits.
        var holding = ui.SendAsync(id => new SearchTicketsCommand { RequestId = id, Query = LargeQuery });
        await Task.Delay(200);
        using var closing = new CancellationTokenSource();
        var waiting = ui.SendAsync(id => new GetSessionCommand { RequestId = id, SessionId = "s1" }, closing.Token);
        await closing.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Soon));

        var after = ui.SendAsync(id => new GetStateCommand { RequestId = id });

        var first = Assert.IsType<SearchTicketsCommand>(await ReadFrameAsync(service));
        var second = await ReadFrameAsync(service);
        Assert.IsType<GetStateCommand>(second);

        await IpcFraming.WriteAsync<IpcEvent>(service, new CommandResult { RequestId = first.RequestId, Ok = true });
        await IpcFraming.WriteAsync<IpcEvent>(service, new CommandResult { RequestId = second.RequestId, Ok = true });
        Assert.True((await holding.WaitAsync(Soon)).Ok);
        Assert.True((await after.WaitAsync(Soon)).Ok);
    }

    private static async Task<IpcCommand> ReadFrameAsync(Stream service)
    {
        try
        {
            return await IpcFraming.ReadAsync<IpcCommand>(service).WaitAsync(Soon)
                ?? throw new InvalidOperationException("The client hung up.");
        }
        catch (TimeoutException)
        {
            Assert.Fail("The service is still waiting for the rest of a frame: a request was cut off partway.");
            throw;
        }
    }

    private static async Task<(NamedPipeServerStream Service, IpcClient Ui)> ConnectByHandAsync()
    {
        var pipeName = IpcPipeNames.ForUser("cancel-" + Guid.NewGuid().ToString("N"));
        var service = IpcServer.DefaultPipeFactory(pipeName)();
        var accepted = service.WaitForConnectionAsync();
        var connecting = IpcClient.ConnectAsync(pipeName, IpcToken.Generate(), "test-ui", serverVerifier: null, "0.0.1", Soon);
        await accepted.WaitAsync(Soon);

        Assert.IsType<HelloCommand>(await IpcFraming.ReadAsync<IpcCommand>(service).WaitAsync(Soon));
        await IpcFraming.WriteAsync<IpcEvent>(
            service,
            new HelloAck
            {
                RequestId = 0,
                ContractVersion = IpcContract.Version,
                ServiceVersion = "test",
                State = new CaptureStateSnapshot { State = CaptureStates.Idle },
            });

        return (service, await connecting.WaitAsync(Soon));
    }
}
