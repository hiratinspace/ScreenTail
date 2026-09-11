namespace ScreenTail.Spike.Core.Tests;

public class TypingBurstAggregatorTests
{
    [Fact]
    public void TypedPassword_BecomesOneBurstWithCountOnly()
    {
        var aggregator = new TypingBurstAggregator();
        var emitted = new List<KeyboardEvent>();

        for (var i = 0; i < 7; i++)
        {
            emitted.AddRange(aggregator.OnKey(KeyCategory.Character, 1000 + (i * 100)));
        }

        Assert.Empty(emitted);
        Assert.Equal(new KeyboardEvent(KeyboardEvent.TypingBurst, 7, 1000), aggregator.Flush());
    }

    [Fact]
    public void GapLongerThanThreshold_SplitsBursts()
    {
        var aggregator = new TypingBurstAggregator(burstGapMs: 1500);
        aggregator.OnKey(KeyCategory.Character, 0);
        aggregator.OnKey(KeyCategory.Character, 100);

        var emitted = aggregator.OnKey(KeyCategory.Character, 2000);

        Assert.Equal(new KeyboardEvent(KeyboardEvent.TypingBurst, 2, 0), Assert.Single(emitted));
        Assert.Equal(new KeyboardEvent(KeyboardEvent.TypingBurst, 1, 2000), aggregator.Flush());
    }

    [Fact]
    public void Enter_ClosesBurstThenEmitsEnter()
    {
        var aggregator = new TypingBurstAggregator();
        aggregator.OnKey(KeyCategory.Character, 10);
        aggregator.OnKey(KeyCategory.Character, 20);

        var emitted = aggregator.OnKey(KeyCategory.Enter, 30);

        Assert.Equal(
            [new KeyboardEvent(KeyboardEvent.TypingBurst, 2, 10), new KeyboardEvent(KeyboardEvent.Enter, 1, 30)],
            emitted);
        Assert.Null(aggregator.Flush());
    }

    [Fact]
    public void Shortcut_ClosesBurstThenEmitsShortcut()
    {
        var aggregator = new TypingBurstAggregator();
        aggregator.OnKey(KeyCategory.Character, 10);

        var emitted = aggregator.OnKey(KeyCategory.Shortcut, 20);

        Assert.Equal(
            [new KeyboardEvent(KeyboardEvent.TypingBurst, 1, 10), new KeyboardEvent(KeyboardEvent.Shortcut, 1, 20)],
            emitted);
    }

    [Fact]
    public void ModifierAndOther_NeitherEmitNorCloseBurst()
    {
        var aggregator = new TypingBurstAggregator();
        aggregator.OnKey(KeyCategory.Character, 10);

        Assert.Empty(aggregator.OnKey(KeyCategory.Modifier, 20));
        Assert.Empty(aggregator.OnKey(KeyCategory.Other, 30));
        aggregator.OnKey(KeyCategory.Character, 40);

        Assert.Equal(new KeyboardEvent(KeyboardEvent.TypingBurst, 2, 10), aggregator.Flush());
    }

    [Fact]
    public void Poll_ClosesBurstOnlyAfterGap()
    {
        var aggregator = new TypingBurstAggregator(burstGapMs: 1500);
        aggregator.OnKey(KeyCategory.Character, 0);

        Assert.Null(aggregator.Poll(1000));
        Assert.Equal(new KeyboardEvent(KeyboardEvent.TypingBurst, 1, 0), aggregator.Poll(1600));
        Assert.Null(aggregator.Flush());
    }

    [Fact]
    public void RecordsLeavingTheHookLayer_HaveNoFieldThatCouldHoldAKey()
    {
        // INV-2 by construction: if someone adds a key/char field, this fails.
        Assert.Equal(
            ["Count", "TsMs", "Type"],
            typeof(KeyboardEvent).GetProperties().Select(p => p.Name).Order());
        Assert.Equal(
            ["Category", "EventTick", "Kind", "X", "Y"],
            typeof(InputSample).GetProperties().Select(p => p.Name).Order());
    }

    [Fact]
    public void OnlySpecTypesAreEverEmitted()
    {
        var aggregator = new TypingBurstAggregator();
        var emitted = new List<KeyboardEvent>();
        long ts = 0;
        foreach (var category in Enum.GetValues<KeyCategory>())
        {
            emitted.AddRange(aggregator.OnKey(category, ts += 10));
        }

        emitted.Add(aggregator.Flush() ?? new KeyboardEvent(KeyboardEvent.Enter, 1, ts));

        Assert.All(
            emitted,
            e => Assert.Contains(e.Type, new[] { KeyboardEvent.TypingBurst, KeyboardEvent.Shortcut, KeyboardEvent.Enter }));
    }
}
