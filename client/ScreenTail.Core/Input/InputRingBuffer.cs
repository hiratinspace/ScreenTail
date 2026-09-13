using System.Runtime.CompilerServices;

namespace ScreenTail.Core.Input;

/// <summary>
/// A bounded single-producer, single-consumer ring buffer for input signals (ST-024).
///
/// The producer is a low-level hook callback, which sits on the path every keystroke and mouse move in the
/// system travels down: if it blocks, the whole machine stutters, and if it takes too long Windows silently
/// removes the hook. So <see cref="TryWrite"/> takes no lock, allocates nothing, and never waits. When the
/// consumer falls behind, the oldest signal is dropped and counted — losing a click is a flaw in a note,
/// while stalling the input path is a flaw in the user's day.
///
/// Bounded on purpose: a consumer that stops draining costs a fixed 64 KB rather than growing until
/// something dies.
/// </summary>
public sealed class InputRingBuffer
{
    private readonly InputSignal[] _slots;
    private readonly int _mask;
    private long _written;
    private long _read;
    private long _dropped;

    /// <param name="capacity">Rounded up to a power of two so the index wrap is a mask rather than a division.</param>
    public InputRingBuffer(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        var size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)capacity);
        _slots = new InputSignal[size];
        _mask = size - 1;
    }

    public int Capacity => _slots.Length;

    /// <summary>Signals thrown away because the consumer fell behind. Reported in diagnostics, never silently.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public int Count => (int)(Interlocked.Read(ref _written) - Interlocked.Read(ref _read));

    /// <summary>Whether there is room for another signal without discarding one. For a producer that can wait.</summary>
    public bool HasRoom => Volatile.Read(ref _written) - Volatile.Read(ref _read) < _slots.Length;

    /// <summary>
    /// Writes one signal, always. Called from the hook callback only.
    /// </summary>
    /// <returns>
    /// False when the buffer was full and the oldest signal was discarded to make room. Not "the write was
    /// refused" — a hook callback has nowhere to put a rejected signal and must never retry, so the write
    /// always happens and the return value reports what it cost.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Write(in InputSignal signal)
    {
        var written = Volatile.Read(ref _written);
        var read = Volatile.Read(ref _read);
        var full = written - read >= _slots.Length;
        if (full)
        {
            // Drop the oldest rather than the newest: the most recent clicks are the ones a note needs.
            Volatile.Write(ref _read, read + 1);
            Interlocked.Increment(ref _dropped);
        }

        _slots[(int)(written & _mask)] = signal;

        // Publish the slot only after it is filled, so the consumer never reads a half-written entry.
        Volatile.Write(ref _written, written + 1);
        return !full;
    }

    /// <summary>
    /// Moves everything buffered into <paramref name="destination"/>, oldest first, and returns how many.
    /// Called from the consumer only.
    /// </summary>
    public int Drain(Span<InputSignal> destination)
    {
        var read = Volatile.Read(ref _read);
        var written = Volatile.Read(ref _written);
        var available = (int)Math.Min(written - read, destination.Length);
        for (var i = 0; i < available; i++)
        {
            destination[i] = _slots[(int)((read + i) & _mask)];
        }

        Volatile.Write(ref _read, read + available);
        return available;
    }
}
