using ScreenTail.Core.Intel;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Intel;

/// <summary>ST-066: active duration minus pauses, with the tenant's increment and minimum.</summary>
public sealed class TimeEntryCalculatorTests
{
    [Theory]
    // The ticket's example: 23 active minutes on 15-minute increments bills half an hour.
    [InlineData(23, 15, 30)]
    [InlineData(1, 15, 15)]
    [InlineData(15, 15, 15)]
    [InlineData(15.5, 15, 30)]
    [InlineData(60, 15, 60)]
    [InlineData(23, 6, 24)]
    [InlineData(2, 6, 6)]
    [InlineData(18.1, 6, 24)]
    public void RoundsUpToTheTenantsIncrement(double activeMinutes, int increment, int expected) =>
        Assert.Equal(
            expected,
            TimeEntryCalculator.SuggestMinutes(
                TimeSpan.FromMinutes(activeMinutes),
                new TimeEntryOptions { IncrementMinutes = increment, MinimumMinutes = increment }));

    [Fact]
    public void MinimumAppliesBeforeRoundingSoTheResultIsWholeIncrements()
    {
        var options = new TimeEntryOptions { IncrementMinutes = 6, MinimumMinutes = 15 };

        Assert.Equal(18, TimeEntryCalculator.SuggestMinutes(TimeSpan.FromMinutes(2), options));
        Assert.Equal(18, TimeEntryCalculator.SuggestMinutes(TimeSpan.FromMinutes(15), options));
        Assert.Equal(24, TimeEntryCalculator.SuggestMinutes(TimeSpan.FromMinutes(19), options));
    }

    [Fact]
    public void NearestRoundingIsAvailableForShopsThatUseIt()
    {
        var options = new TimeEntryOptions { IncrementMinutes = 15, MinimumMinutes = 0, Rounding = TimeRounding.Nearest };

        Assert.Equal(15, TimeEntryCalculator.SuggestMinutes(TimeSpan.FromMinutes(20), options));
        Assert.Equal(30, TimeEntryCalculator.SuggestMinutes(TimeSpan.FromMinutes(23), options));
        Assert.Equal(30, TimeEntryCalculator.SuggestMinutes(TimeSpan.FromMinutes(22.5), options));
    }

    [Fact]
    public void ASessionWithNoActiveTimeSuggestsNothing()
    {
        Assert.Equal(0, TimeEntryCalculator.SuggestMinutes(TimeSpan.Zero));
        Assert.Equal(0, TimeEntryCalculator.SuggestMinutes(TimeSpan.FromSeconds(-5)));
        Assert.Equal(15, TimeEntryCalculator.SuggestMinutes(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void AnIncrementOfZeroIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TimeEntryCalculator.SuggestMinutes(TimeSpan.FromMinutes(10), new TimeEntryOptions { IncrementMinutes = 0 }));

    [Fact]
    public void PausedMinutesAreExcluded()
    {
        // AC2: 32 minutes of wall clock, 9 of them paused, leaves 23 active — billed as 30, not 45.
        var session = new PausableSession();

        session.Record(TimeSpan.FromMinutes(14));
        session.Pause(TimeSpan.FromMinutes(9));
        session.Record(TimeSpan.FromMinutes(9));

        Assert.Equal(TimeSpan.FromMinutes(32), session.Wall);
        Assert.Equal(30, TimeEntryCalculator.SuggestMinutes(session.ActiveMs));
        Assert.Equal(45, TimeEntryCalculator.SuggestMinutes((long)session.Wall.TotalMilliseconds));
    }

    [Fact]
    public async Task TheDurationTheStateMachineStoresIsTheActiveTime()
    {
        // The calculator's input is FinalizeInfo.DurationMs, and the machine's accounting is what excludes pauses.
        await using var harness = await MachineHarness.StartAsync();

        await harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp });
        await Task.Delay(120);
        await harness.Machine.PauseAsync();
        await Task.Delay(250);
        await harness.Machine.ResumeAsync();
        await Task.Delay(120);
        await harness.Machine.StopAsync();

        var stored = (await harness.Store.LoadSessionAsync(harness.Machine.SessionId!))!;
        Assert.NotNull(stored.DurationMs);
        Assert.InRange(stored.DurationMs.Value, 200, 480); // ~240 ms recording, not the ~490 ms of wall clock
    }

    /// <summary>Mirrors the machine's accounting rule — recording time counts, paused time doesn't.</summary>
    private sealed class PausableSession
    {
        public TimeSpan Wall { get; private set; }

        public long ActiveMs { get; private set; }

        public void Record(TimeSpan span)
        {
            Wall += span;
            ActiveMs += (long)span.TotalMilliseconds;
        }

        public void Pause(TimeSpan span) => Wall += span;
    }
}
