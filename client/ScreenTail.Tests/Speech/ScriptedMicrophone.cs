using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ScreenTail.Core.Speech;

namespace ScreenTail.Tests.Speech;

/// <summary>
/// A microphone with devices the test plugs and unplugs, frames the test feeds, and a count of how many
/// times it is open right now, which is the thing Windows' microphone-in-use light follows.
/// </summary>
internal sealed class ScriptedMicrophone : IMicrophone
{
    public const string HeadsetId = "{0.0.1.00000000}.{headset}";
    public const string BuiltInId = "{0.0.1.00000000}.{built-in}";

    private readonly Lock _gate = new();
    private Channel<short[]>? _current;
    private int _open;

    public List<MicrophoneDevice> Connected { get; } =
    [
        new(HeadsetId, "Headset", IsDefault: false),
        new(BuiltInId, "Built-in microphone", IsDefault: true),
    ];

    /// <summary>Every device id a listen opened, in order.</summary>
    public List<string?> OpenedIds { get; } = [];

    /// <summary>How many listens are open now.</summary>
    public int Open => Volatile.Read(ref _open);

    public string? DeviceName => MicrophoneChoice.Resolve(Connected, null).Device?.Name;

    public int SampleRate => 16_000;

    public IReadOnlyList<MicrophoneDevice> Devices() => [.. Connected];

    public IAsyncEnumerable<ReadOnlyMemory<short>> ListenAsync(CancellationToken ct = default) => ListenToAsync(null, ct);

    public async IAsyncEnumerable<ReadOnlyMemory<short>> ListenToAsync(string? deviceId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var frames = Channel.CreateUnbounded<short[]>();
        lock (_gate)
        {
            OpenedIds.Add(deviceId);
            _current = frames;
        }

        _ = Interlocked.Increment(ref _open);
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return frame;
            }
        }
        finally
        {
            _ = Interlocked.Decrement(ref _open);
        }
    }

    /// <summary>One frame into whichever listen is open now.</summary>
    public void Feed(short[] frame)
    {
        lock (_gate)
        {
            _ = _current?.Writer.TryWrite(frame);
        }
    }

    /// <summary>The open device goes away: the listen ends by itself.</summary>
    public void Unplug()
    {
        lock (_gate)
        {
            _ = _current?.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static short[] Loud() => [.. Enumerable.Repeat((short)16_000, 160)];
}
