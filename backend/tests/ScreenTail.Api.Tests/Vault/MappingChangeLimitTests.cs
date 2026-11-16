using ScreenTail.Api.Vault;

namespace ScreenTail.Api.Tests.Vault;

/// <summary>
/// The per-device mapping limit (2026-11-15 review): the check and the take are one step, so requests
/// arriving together cannot all pass a count none of them has added to yet.
/// </summary>
public sealed class MappingChangeLimitTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task RequestsArrivingTogetherGetExactlyTheLimit()
    {
        var limit = new MappingChangeLimit(new Clock());
        var device = Guid.NewGuid();
        using var start = new ManualResetEventSlim();

        var tries = Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            start.Wait(TestContext.Current.CancellationToken);
            return limit.TryTake(device);
        })).ToList();
        start.Set();
        var answers = await Task.WhenAll(tries);

        Assert.Equal(MappingChangeLimit.PerDevice, answers.Count(a => a is null));
    }

    [Fact]
    public void TheThirtyFirstWaitsForTheWindowAndOtherDevicesAreNotAffected()
    {
        var clock = new Clock();
        var limit = new MappingChangeLimit(clock);
        var device = Guid.NewGuid();
        for (var i = 0; i < MappingChangeLimit.PerDevice; i++)
        {
            Assert.Null(limit.TryTake(device));
            clock.Now += TimeSpan.FromSeconds(1);
        }

        var wait = limit.TryTake(device);
        var other = limit.TryTake(Guid.NewGuid());
        clock.Now += wait!.Value;
        var later = limit.TryTake(device);

        Assert.InRange(wait.Value, TimeSpan.FromMinutes(9), MappingChangeLimit.Window);
        Assert.Null(other);
        Assert.Null(later);
    }
}
