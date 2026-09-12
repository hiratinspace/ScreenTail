namespace ScreenTail.Spike.Core.Tests;

public class SpscRingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(100)]
    public void Capacity_MustBePowerOfTwo(int capacity)
    {
        Assert.Throws<ArgumentException>(() => new SpscRing<int>(capacity));
    }

    [Fact]
    public void ReadsInWriteOrder()
    {
        var ring = new SpscRing<int>(4);
        ring.TryWrite(1);
        ring.TryWrite(2);
        ring.TryWrite(3);

        Assert.True(ring.TryRead(out var a));
        Assert.True(ring.TryRead(out var b));
        Assert.True(ring.TryRead(out var c));
        Assert.False(ring.TryRead(out _));
        Assert.Equal([1, 2, 3], new[] { a, b, c });
    }

    [Fact]
    public void Full_DropsAndCounts()
    {
        var ring = new SpscRing<int>(2);
        Assert.True(ring.TryWrite(1));
        Assert.True(ring.TryWrite(2));

        Assert.False(ring.TryWrite(3));
        Assert.Equal(1, ring.Dropped);
    }

    [Fact]
    public void WrapsAround()
    {
        var ring = new SpscRing<int>(4);
        for (var i = 0; i < 50; i++)
        {
            Assert.True(ring.TryWrite(i));
            Assert.True(ring.TryRead(out var value));
            Assert.Equal(i, value);
        }
    }

    [Fact]
    public async Task ConcurrentProducerAndConsumer_LoseNothingWhenNotFull()
    {
        const int count = 200_000;
        var ring = new SpscRing<long>(1 << 10);

        var producer = Task.Run(() =>
        {
            for (long i = 1; i <= count; i++)
            {
                while (!ring.TryWrite(i))
                {
                    Thread.SpinWait(1);
                }
            }
        });

        long sum = 0;
        long previous = 0;
        var received = 0;
        while (received < count)
        {
            if (ring.TryRead(out var value))
            {
                Assert.Equal(previous + 1, value);
                previous = value;
                sum += value;
                received++;
            }
        }

        await producer;
        Assert.Equal((long)count * (count + 1) / 2, sum);
    }
}
