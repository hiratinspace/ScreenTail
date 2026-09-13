using System.Diagnostics;
using System.Reflection;
using ScreenTail.Core.Input;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Input;

/// <summary>ST-024: the buffer's behaviour under pressure, how signals become events, and INV-2 by construction.</summary>
public sealed class InputCaptureTests
{
    private static long _clock;

    [Fact]
    public void NothingInTheSignalTypeCanHoldAKeystroke()
    {
        // INV-2 as a structural fact rather than a habit: adding a field able to carry a key would fail here,
        // which is the point — the next person to touch this has to confront the invariant deliberately.
        var fields = typeof(InputSignal).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.Equal(
            ["Button", "Kind", "Timestamp", "X", "Y"],
            fields.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(fields, f => Assert.True(
            f.PropertyType.IsPrimitive || f.PropertyType.IsEnum,
            $"{f.Name} is {f.PropertyType.Name}; only numbers and categories belong here"));
        Assert.DoesNotContain(fields, f => f.PropertyType == typeof(string) || f.PropertyType == typeof(char));
    }

    [Fact]
    public void TypingAPasswordLeavesOnlyACount()
    {
        // The acceptance criterion, in the form it actually matters: every key of "Winter2026!" goes in,
        // and one typing_burst with a number comes out.
        const string Password = "Winter2026!";
        var buffer = new InputRingBuffer(64);
        foreach (var _ in Password)
        {
            buffer.Write(new InputSignal(InputKind.PrintableKey, Tick()));
        }

        buffer.Write(new InputSignal(InputKind.Enter, Tick()));
        var events = ReadAll(buffer);

        var burst = Assert.IsType<TypingBurstEvent>(events[0]);
        Assert.Equal(Password.Length, burst.CharCount);
        Assert.IsType<EnterEvent>(events[1]);

        // Nothing anywhere in the serialized events resembles the password or any of its characters.
        var json = SessionJson.Serialize(SessionWith(events));
        Assert.DoesNotContain("Winter", json, StringComparison.OrdinalIgnoreCase);
        foreach (var character in Password.Distinct().Where(char.IsLetterOrDigit))
        {
            Assert.DoesNotContain($"\"{character}\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AClickKeepsWhereAndWhichButton()
    {
        var buffer = new InputRingBuffer(8);
        buffer.Write(new InputSignal(InputKind.Click, Tick(), 1280, -400, MouseButtonKind.Right));

        var click = Assert.IsType<ClickEvent>(Assert.Single(ReadAll(buffer)));

        Assert.Equal(1280, click.X);
        Assert.Equal(-400, click.Y); // a monitor above the primary one: negative is valid, not a bug
        Assert.Equal(MouseButton.Right, click.Button);
    }

    [Fact]
    public void APauseSplitsTyping()
    {
        var reader = new InputSignalReader(burstGap: TimeSpan.FromSeconds(2));
        InputSignal[] signals =
        [
            new(InputKind.PrintableKey, Ms(0)),
            new(InputKind.PrintableKey, Ms(200)),
            new(InputKind.PrintableKey, Ms(5_000)),  // after a think
            new(InputKind.PrintableKey, Ms(5_100)),
        ];

        var events = reader.Read(signals, ToMs, Between).ToList();
        events.AddRange(reader.Flush(ToMs) is { } tail ? [tail] : Array.Empty<SessionEvent>());

        Assert.Equal(2, events.Count);
        Assert.Equal(2, Assert.IsType<TypingBurstEvent>(events[0]).CharCount);
        Assert.Equal(2, Assert.IsType<TypingBurstEvent>(events[1]).CharCount);
        Assert.Equal(0, events[0].TsMs);
        Assert.Equal(5_000, events[1].TsMs);
    }

    [Fact]
    public void AShortcutEndsTheBurstAndSaysNothingElse()
    {
        var reader = new InputSignalReader();
        InputSignal[] signals =
        [
            new(InputKind.PrintableKey, Ms(0)),
            new(InputKind.PrintableKey, Ms(50)),
            new(InputKind.Shortcut, Ms(100)),
        ];

        var events = reader.Read(signals, ToMs, Between).ToList();

        Assert.Equal(2, Assert.IsType<TypingBurstEvent>(events[0]).CharCount);
        var shortcut = Assert.IsType<ShortcutEvent>(events[1]);
        // The schema gives a shortcut a timestamp and nothing else. Which combination was pressed is not
        // recorded anywhere, so "Ctrl+V into a password box" cannot be reconstructed.
        Assert.Equal(100, shortcut.TsMs);
        Assert.DoesNotContain(typeof(ShortcutEvent).GetProperties(), p => p.PropertyType == typeof(string) && p.Name != "Type");
    }

    [Fact]
    public void TheBufferDropsTheOldestRatherThanBlockingTheInputPath()
    {
        var buffer = new InputRingBuffer(4);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(buffer.Write(new InputSignal(InputKind.Click, Tick(), i, 0)));
        }

        // Full. The fifth write still happens — it reports the drop rather than refusing, because a hook
        // callback has nowhere to keep a signal it was not allowed to write.
        Assert.False(buffer.HasRoom);
        Assert.False(buffer.Write(new InputSignal(InputKind.Click, Tick(), 99, 0)));
        Assert.Equal(1, buffer.Dropped);

        var drained = new InputSignal[8];
        var count = buffer.Drain(drained);
        Assert.Equal(4, count);
        Assert.Equal(1, drained[0].X);  // the oldest went, not the newest
        Assert.Equal(99, drained[3].X);
    }

    [Fact]
    public void CapacityIsRoundedToAPowerOfTwo()
    {
        // The index wrap is a mask rather than a division, which is why the size has to be a power of two.
        Assert.Equal(4096, new InputRingBuffer(4096).Capacity);
        Assert.Equal(128, new InputRingBuffer(100).Capacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new InputRingBuffer(1));
    }

    [Fact]
    public async Task WritingWhileDrainingLosesNothing()
    {
        // One producer, one consumer, no lock. Everything written must come out exactly once, in order.
        const int Total = 50_000;
        var buffer = new InputRingBuffer(1024);
        var seen = new List<int>(Total);

        var producer = Task.Run(() =>
        {
            for (var i = 0; i < Total; i++)
            {
                // Only the test waits for room; the real callback never can, which is why Write always
                // writes and reports a drop instead of refusing.
                while (!buffer.HasRoom)
                {
                    Thread.SpinWait(1);
                }

                buffer.Write(new InputSignal(InputKind.Click, i, i, 0));
            }
        });

        var scratch = new InputSignal[256];
        while (seen.Count < Total)
        {
            var drained = buffer.Drain(scratch);
            for (var i = 0; i < drained; i++)
            {
                seen.Add(scratch[i].X);
            }
        }

        await producer;
        Assert.Equal(Total, seen.Count);
        Assert.Equal(0, buffer.Dropped);
        Assert.Equal(Enumerable.Range(0, Total), seen);
    }

    [Fact]
    public void WritingIsCheapEnoughForTheInputPath()
    {
        // Not the real callback — that is measured on the laptop — but this catches an allocation or a lock
        // creeping into the buffer, which is what would actually make the callback slow.
        var buffer = new InputRingBuffer(4096);
        var signal = new InputSignal(InputKind.Click, 0, 1, 2);
        for (var i = 0; i < 1000; i++)
        {
            buffer.Write(signal);
        }

        // Raw timestamps rather than Stopwatch.StartNew, which allocates the Stopwatch itself — 40 bytes
        // that the first version of this test blamed on the buffer.
        var before = GC.GetAllocatedBytesForCurrentThread();

        // Three batches, and the fastest one counts. A lock or an allocation creeping in would slow every
        // batch; a GC pause or the scheduler taking the core slows one. Judging on the total made this
        // fail about once in four runs on a machine doing anything else, and an intermittently red suite
        // is one people stop reading.
        var fastest = double.MaxValue;
        for (var batch = 0; batch < 3; batch++)
        {
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < 100_000; i++)
            {
                buffer.Write(signal);
            }

            fastest = Math.Min(fastest, Stopwatch.GetElapsedTime(start).TotalMilliseconds / 100_000);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(fastest < 0.001, $"{fastest * 1000:F3} µs per write");
    }

    private static Session SessionWith(IReadOnlyList<SessionEvent> events) => new()
    {
        SchemaVersion = "session.v1",
        SessionId = "s",
        StartedAt = new DateTimeOffset(2026, 9, 12, 18, 0, 0, TimeSpan.Zero),
        RemoteTool = new RemoteTool { Kind = RemoteToolKind.Rdp },
        PartialCapture = false,
        FramesPurgedUnredacted = 0,
        LocalOnly = false,
        Events = events,
        Frames = [],
        Transcript = [],
    };

    private static List<SessionEvent> ReadAll(InputRingBuffer buffer)
    {
        var scratch = new InputSignal[buffer.Capacity];
        var count = buffer.Drain(scratch);
        var reader = new InputSignalReader();
        var events = reader.Read(scratch.AsMemory(0, count), ToMs, Between).ToList();
        if (reader.Flush(ToMs) is { } tail)
        {
            events.Add(tail);
        }

        return events;
    }

    private static long Tick() => Interlocked.Add(ref _clock, TimeSpan.TicksPerMillisecond);

    private static long Ms(long milliseconds) => milliseconds * TimeSpan.TicksPerMillisecond;

    private static long ToMs(long timestamp) => timestamp / TimeSpan.TicksPerMillisecond;

    private static TimeSpan Between(long from, long to) => TimeSpan.FromTicks(to - from);
}
