namespace ScreenTail.Spike.Core;

/// <summary>
/// Single-producer/single-consumer ring buffer. The producer is the hook thread, so a write must never
/// block or allocate; when full, the sample is dropped and counted instead.
/// </summary>
public sealed class SpscRing<T>
    where T : struct
{
    private readonly T[] _items;
    private readonly int _mask;
    private long _head;
    private long _tail;
    private long _dropped;

    public SpscRing(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of two, at least 2.", nameof(capacity));
        }

        _items = new T[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _items.Length;

    public long Dropped => Interlocked.Read(ref _dropped);

    public bool TryWrite(in T item)
    {
        var tail = _tail;
        if (tail - Volatile.Read(ref _head) >= _items.Length)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        _items[tail & _mask] = item;
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    public bool TryRead(out T item)
    {
        var head = _head;
        if (head >= Volatile.Read(ref _tail))
        {
            item = default;
            return false;
        }

        item = _items[head & _mask];
        Volatile.Write(ref _head, head + 1);
        return true;
    }
}
