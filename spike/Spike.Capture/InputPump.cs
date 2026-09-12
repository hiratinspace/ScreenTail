using ScreenTail.Spike.Core;

namespace ScreenTail.Spike.Capture;

/// <summary>Drains the hook ring off the hook thread: clicks go to screenshots, keys become burst counts.</summary>
internal sealed class InputPump(SpscRing<InputSample> ring, ScreenshotPipeline shots, RunReport report)
{
    private readonly TypingBurstAggregator _aggregator = new();

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                Drain();
            }
        }
        catch (OperationCanceledException)
        {
        }

        Drain();
        Record(_aggregator.Flush());
    }

    private void Drain()
    {
        while (ring.TryRead(out var sample))
        {
            if (sample.Kind == InputKind.Click)
            {
                report.Clicks++;
                shots.OnClick(sample.EventTick);
                continue;
            }

            foreach (var keyboardEvent in _aggregator.OnKey(sample.Category, sample.EventTick))
            {
                report.CountKeyboard(keyboardEvent);
            }
        }

        Record(_aggregator.Poll(unchecked((uint)Environment.TickCount)));
    }

    private void Record(KeyboardEvent? keyboardEvent)
    {
        if (keyboardEvent is not null)
        {
            report.CountKeyboard(keyboardEvent);
        }
    }
}
