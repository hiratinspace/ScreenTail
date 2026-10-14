using System.Buffers.Binary;
using System.Text;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Tests.Ipc;

public class IpcFramingTests
{
    [Fact]
    public async Task RoundTripsCommandsAndEvents()
    {
        using var stream = new MemoryStream();
        await IpcFraming.WriteAsync<IpcCommand>(stream, new HelloCommand { RequestId = 7, ContractVersion = 1, Token = "AB", ClientName = "ui" });
        await IpcFraming.WriteAsync<IpcEvent>(stream, new StateChanged { State = new CaptureStateSnapshot { State = CaptureStates.Recording, ElapsedMs = 5 } });
        stream.Position = 0;

        var hello = Assert.IsType<HelloCommand>(await IpcFraming.ReadAsync<IpcCommand>(stream));
        var changed = Assert.IsType<StateChanged>(await IpcFraming.ReadAsync<IpcEvent>(stream));

        Assert.Equal(7, hello.RequestId);
        Assert.Equal("ui", hello.ClientName);
        Assert.Null(hello.ClientVersion);
        Assert.Equal(CaptureStates.Recording, changed.State.State);
        Assert.Equal(5, changed.State.ElapsedMs);
        Assert.Null(await IpcFraming.ReadAsync<IpcCommand>(stream));
    }

    [Fact]
    public async Task EndOfStreamBetweenFramesIsNull()
    {
        using var stream = new MemoryStream();

        Assert.Null(await IpcFraming.ReadAsync<IpcCommand>(stream));
    }

    [Fact]
    public async Task TruncatedFrameThrows()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 10);
        using var stream = new MemoryStream([.. header, .. "{\"t"u8.ToArray()]);

        await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync<IpcCommand>(stream));
    }

    [Fact]
    public async Task OversizedFrameIsRefusedBeforeReadingIt()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, IpcFraming.MaxMessageBytes + 1);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync<IpcCommand>(stream));
    }

    [Theory]
    [InlineData("plain words")]
    [InlineData("<html> & 'quotes' \"escaped\"")] // the default encoder writes these as \uXXXX
    [InlineData("Ünïcödé, 中文, and an emoji 😀")] // non-ASCII is escaped too: one char is six bytes
    [InlineData("")]
    public async Task MeasuredBytesAreExactlyWhatTheFrameCarries(string text)
    {
        // E-20: a reply is refused on this number instead of being written past the limit, so it must be
        // the frame's own length and not an estimate in characters. Measured through the polymorphic base,
        // the way the server writes every event.
        IpcEvent message = new FrameTextLoaded { RequestId = 3, FrameId = "f1", OcrText = text };
        using var stream = new MemoryStream();

        await IpcFraming.WriteAsync(stream, message);

        Assert.Equal(stream.Length - 4, IpcFraming.MeasureBytes(message));
    }

    [Fact]
    public void MeasuringAMessageOverTheLimitDoesNotThrow()
    {
        // The point is to learn the size before the write that would throw.
        IpcEvent message = new FrameTextLoaded { RequestId = 3, FrameId = "f1", OcrText = new string('é', IpcFraming.MaxMessageBytes / 4) };

        Assert.True(IpcFraming.MeasureBytes(message) > IpcFraming.MaxMessageBytes);
    }

    [Theory]
    [InlineData("""{"type":"start","request_id":1,"extra":true}""")] // unknown member
    [InlineData("""{"type":"launch_missiles","request_id":1}""")] // unknown type
    [InlineData("""{"request_id":1}""")] // no discriminator
    [InlineData("""{"type":"hello","request_id":1}""")] // required members missing
    public async Task MalformedMessagesAreRejected(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        using var stream = new MemoryStream([.. header, .. payload]);

        await Assert.ThrowsAsync<IpcProtocolException>(() => IpcFraming.ReadAsync<IpcCommand>(stream));
    }
}
