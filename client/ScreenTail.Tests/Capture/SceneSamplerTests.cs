using ScreenTail.Core.Capture;

namespace ScreenTail.Tests.Capture;

/// <summary>
/// ST-026 against recorded sequences: a minute, or twenty, of screens played through the sampler at one a
/// second, counting what came out. Every acceptance criterion here is a total over time rather than a
/// single decision, and the screens are real proportions rather than random grids — see
/// <see cref="SyntheticScreen"/> for why that distinction decides whether these tests mean anything.
/// </summary>
public sealed class SceneSamplerTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AStillScreenForASolidMinuteProducesNothing()
    {
        // The criterion that decides whether the feature is usable at all: a technician reading a page for a
        // minute must not get sixty screenshots of it.
        var clock = new ManualTime(At);
        var sampler = new SceneSampler(clock);
        var still = SyntheticScreen.Desktop().Hash();

        var kept = Play(sampler, clock, Enumerable.Repeat(still, 60));

        Assert.Equal(0, kept);
        Assert.Equal(59, sampler.Counts.Unchanged);
    }

    [Fact]
    public void ACaretBlinkingAndAClockTickingForAMinuteProduceNothing()
    {
        // A real screen is never still. If this produced frames, an idle desktop would be photographed once
        // a second all day and the useful frames would be buried in them.
        var clock = new ManualTime(At);
        var sampler = new SceneSampler(clock);

        var kept = Play(sampler, clock, Enumerable.Range(0, 60).Select(second =>
            SyntheticScreen.Desktop()
                .Block(300, 200, 2, 18, shade: (byte)(second % 2 == 0 ? 20 : 243))    // the caret
                .Block(1800, 1045, 60, 20, shade: (byte)(40 + (second % 10)))          // the clock
                .Hash()));

        Assert.Equal(0, kept);
        Assert.Equal(59, sampler.Counts.Unchanged);
    }

    [Fact]
    public void AnErrorDialogAppearingIsCapturedWithinTwoSeconds()
    {
        var clock = new ManualTime(At);
        var sampler = new SceneSampler(clock);
        var appearedAt = At + TimeSpan.FromSeconds(10);
        DateTimeOffset? captured = null;

        for (var second = 0; second < 30; second++)
        {
            var now = At + TimeSpan.FromSeconds(second);
            var screen = SyntheticScreen.Desktop();
            if (now >= appearedAt)
            {
                screen.Window((SyntheticScreen.Width - 400) / 2, (SyntheticScreen.Height - 300) / 2, 400, 300);
            }

            if (sampler.Offer(screen.Hash()) == SceneDecision.Keep && captured is null)
            {
                captured = now;
            }

            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.NotNull(captured);
        Assert.True(
            captured - appearedAt <= TimeSpan.FromSeconds(2),
            $"a 400x300 dialog appeared and was not captured for {(captured - appearedAt)!.Value.TotalSeconds:F0} s");
    }

    [Fact]
    public void OneWindowOpeningIsOneScreenshotAndNotFour()
    {
        // A window opening is not instant: it paints, lays out, fills in, settles. Each stage differs from
        // the last frame kept, and all of them are the same event.
        var clock = new ManualTime(At);
        var sampler = new SceneSampler(clock);

        _ = sampler.Offer(SyntheticScreen.Desktop().Hash());
        clock.Advance(TimeSpan.FromSeconds(1));

        var kept = 0;
        foreach (var height in new[] { 120, 220, 300, 300 })
        {
            var stage = SyntheticScreen.Desktop()
                .Window((SyntheticScreen.Width - 400) / 2, (SyntheticScreen.Height - 300) / 2, 400, height);
            if (sampler.Offer(stage.Hash()) == SceneDecision.Keep)
            {
                kept++;
            }

            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, kept);
    }

    [Fact]
    public void ABusyTwentyMinutesStaysUnderSixtyFrames()
    {
        // ST-005 budgets under 40 MB for a twenty-minute session. That holds only if a screen which
        // genuinely changes every second — a log scrolling, a progress bar, a video call — is still capped.
        var clock = new ManualTime(At);
        var sampler = new SceneSampler(clock);

        var kept = Play(sampler, clock, Enumerable.Range(0, 20 * 60).Select(Busy));

        Assert.True(kept <= 60, $"a busy twenty minutes produced {kept} scene frames against a budget of 60");
        Assert.True(kept > 0, "a screen that changed every second should produce some frames");
    }

    [Fact]
    public void TheBudgetIsASlidingWindowAndNotAHardStop()
    {
        // Spending the budget early must not leave the rest of a long session blind.
        var clock = new ManualTime(At);
        var sampler = new SceneSampler(clock);

        var first = Play(sampler, clock, Enumerable.Range(0, 20 * 60).Select(Busy));
        var second = Play(sampler, clock, Enumerable.Range(20 * 60, 20 * 60).Select(Busy));

        Assert.True(first > 0 && second > 0, $"first twenty minutes kept {first}, second kept {second}");
        Assert.True(second <= 60);
    }

    [Fact]
    public void AWindowGrowingSlowlyIsEventuallyNoticed()
    {
        // The baseline is the last frame kept, not the last sample seen. Comparing against the last sample
        // would let a screen change completely, a few pixels at a time, without ever tripping the threshold.
        var clock = new ManualTime(At);
        var sampler = new SceneSampler(clock);

        _ = sampler.Offer(SyntheticScreen.Desktop().Hash());
        clock.Advance(TimeSpan.FromSeconds(1));

        var kept = 0;
        for (var step = 1; step <= 40; step++)
        {
            var growing = SyntheticScreen.Desktop().Window(400, 300, step * 12, step * 8);
            if (sampler.Offer(growing.Hash()) == SceneDecision.Keep)
            {
                kept++;
            }

            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(kept > 0, "a window that grew to fill the screen a few pixels at a time was never captured");
    }

    [Fact]
    public void TheFirstSampleIsABaselineAndNotAScreenshot()
    {
        var sampler = new SceneSampler(new ManualTime(At));

        Assert.Equal(SceneDecision.Baseline, sampler.Offer(SyntheticScreen.Desktop().Hash()));
        Assert.Equal(0, sampler.Counts.Kept);
    }

    [Fact]
    public void AFreshSessionStartsWithNoHistory()
    {
        var clock = new ManualTime(At);
        var sampler = new SceneSampler(clock);
        _ = sampler.Offer(SyntheticScreen.Desktop().Hash());

        sampler.Reset();

        Assert.Equal(SceneDecision.Baseline, sampler.Offer(SyntheticScreen.Desktop().Hash()));
        Assert.Equal(0, sampler.Counts.Unchanged);
    }

    [Fact]
    public void EverySampleIsAccountedFor()
    {
        // The diagnostics panel adds these up, and a sample that fell through without being counted would
        // show as a screenshot nobody can explain the absence of.
        var clock = new ManualTime(At);
        var sampler = new SceneSampler(clock);

        _ = Play(sampler, clock, Enumerable.Range(0, 600).Select(Busy));

        var counts = sampler.Counts;
        Assert.Equal(600, counts.Seen);
        Assert.Equal(
            counts.Seen,
            counts.Kept + counts.Unchanged + counts.TooSoon + counts.OverBudget + 1);   // + the baseline
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public void AFlatGridHashesToNothingSet(byte shade)
    {
        // Every cell equal means no cell is brighter than its neighbour. A blank screen is a valid answer,
        // not a special case, and two blank screens compare as identical whatever shade they are.
        var flat = new byte[PerceptualHash.GridLength];
        Array.Fill(flat, shade);

        Assert.Equal(SceneHash.Blank, PerceptualHash.OfGrid(flat));
    }

    [Fact]
    public void AGridOfTheWrongSizeIsRejected()
    {
        Assert.Throws<ArgumentException>(() => PerceptualHash.OfGrid(new byte[PerceptualHash.GridLength - 1]));
    }

    /// <summary>
    /// A genuinely different screen every second: windows of different sizes in different places, the way a
    /// screen looks while someone works quickly through a problem.
    ///
    /// Not a scrolling log of uniform bands, which is what this was first written as and which the sampler
    /// correctly ignores — see <see cref="SceneGridTests.ARegionChangingShadeWithoutChangingOrderIsNotSeen"/>.
    /// A sequence the sampler ignores would have made the budget assertions below pass while measuring
    /// nothing at all.
    /// </summary>
    private static SceneHash Busy(int second)
    {
        var width = 300 + (second * 37 % 900);
        var height = 200 + (second * 53 % 600);
        return SyntheticScreen.Desktop(BusyScale)
            .Window(second * 29 % (SyntheticScreen.Width - width), second * 17 % (SyntheticScreen.Height - height), width, height)
            .Hash();
    }

    /// <summary>
    /// These sequences play twenty simulated minutes each, and at full resolution that is 1,200 screens of
    /// two megapixels — seventeen seconds of a suite that otherwise runs in one, on every push. The grid is
    /// 17×16 whatever the resolution, so a quarter-size screen produces the same cells from the same
    /// relative areas. The sensitivity tests that need real pixel counts stay at full size.
    /// </summary>
    private const int BusyScale = 4;

    /// <summary>Plays a sequence at one sample a second and returns how many became screenshots.</summary>
    private static int Play(SceneSampler sampler, ManualTime clock, IEnumerable<SceneHash> samples)
    {
        var kept = 0;
        foreach (var sample in samples)
        {
            if (sampler.Offer(sample) == SceneDecision.Keep)
            {
                kept++;
            }

            clock.Advance(TimeSpan.FromSeconds(1));
        }

        return kept;
    }
}
