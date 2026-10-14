using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTail.Shared.Ipc;

/// <summary>
/// One message per frame: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.
/// Reads return null at a clean end of stream and throw on a truncated or oversized frame.
/// </summary>
public static class IpcFraming
{
    public const int MaxMessageBytes = 1024 * 1024;

    public static JsonSerializerOptions Json { get; } = CreateOptions();

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (payload.Length > MaxMessageBytes)
        {
            throw new IpcProtocolException($"Message of {payload.Length} bytes exceeds the {MaxMessageBytes}-byte limit.");
        }

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// How many bytes <see cref="WriteAsync{T}"/> would put in the frame for this message, without building
    /// the frame. Serialized with the same options into a stream that only counts, so a reply can be
    /// refused with a reason before the write would throw and the connection close.
    ///
    /// Pass the type the writer is given (for a reply, <see cref="IpcEvent"/>): the discriminator is only
    /// written when the message is serialized through its polymorphic base.
    /// </summary>
    public static long MeasureBytes<T>(T message)
    {
        using var counter = new CountingStream();
        JsonSerializer.Serialize(counter, message, Json);
        return counter.Length;
    }

    /// <returns>The message, or null when the stream ended before a new frame began.</returns>
    /// <exception cref="IpcProtocolException">The frame is oversized, truncated, or not valid JSON for <typeparamref name="T"/>.</exception>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await FillAsync(stream, header, ct).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length > MaxMessageBytes)
        {
            throw new IpcProtocolException($"Frame of {length} bytes exceeds the {MaxMessageBytes}-byte limit.");
        }

        var payload = new byte[length];
        if (!await FillAsync(stream, payload, ct).ConfigureAwait(false))
        {
            throw new IpcProtocolException("The stream ended inside a frame.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new IpcProtocolException("The message is null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // NotSupportedException: a polymorphic message with no "type" discriminator.
            throw new IpcProtocolException("The message is not valid for this contract.", ex);
        }
    }

    /// <returns>false only when the stream ended before the first byte; a partial read throws.</returns>
    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return filled == 0 ? false : throw new IpcProtocolException("The stream ended inside a frame.");
            }

            filled += read;
        }

        return true;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            AllowOutOfOrderMetadataProperties = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>A write-only stream that keeps nothing and remembers how much it was given.</summary>
internal sealed class CountingStream : Stream
{
    private long _length;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => _length += count;

    public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _length += count;
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _length += buffer.Length;
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

public sealed class IpcProtocolException : Exception
{
    public IpcProtocolException()
    {
    }

    public IpcProtocolException(string message)
        : base(message)
    {
    }

    public IpcProtocolException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
