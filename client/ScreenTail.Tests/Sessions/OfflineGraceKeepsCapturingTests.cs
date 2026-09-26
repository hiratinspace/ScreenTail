using ScreenTail.Core.Net;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Sessions;

/// <summary>
/// ST-010 AC3: the backend being unreachable — for an hour or for a month — changes nothing about
/// capture. The machine has no backend to consult; only drafting and publishing do, and they queue.
/// The grace period is a number the diagnostics can show, not a switch anything trips on.
/// </summary>
public sealed class OfflineGraceKeepsCapturingTests
{
    [Fact]
    public async Task ASessionStartsAndStopsWithNoBackendAtAllAndBeyondTheGrace()
    {
        await using var harness = await MachineHarness.StartAsync();
        var lastContact = DateTimeOffset.UtcNow.AddDays(-30);
        Assert.True(OfflineGrace.Beyond(lastContact, DateTimeOffset.UtcNow));

        Assert.True(await harness.Machine.StartAsync(new RemoteTool { Kind = RemoteToolKind.Rdp }, localOnly: false, policyVersion: null, ct: TestContext.Current.CancellationToken));
        var id = harness.Machine.SessionId!;
        await harness.Machine.StopAsync(TestContext.Current.CancellationToken);

        var session = await harness.Store.LoadSessionAsync(id, TestContext.Current.CancellationToken);
        Assert.NotNull(session);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(6.9, false)]
    [InlineData(7.0, false)]
    [InlineData(7.1, true)]
    public void TheGraceIsSevenDaysFromTheLastAnswer(double daysAgo, bool beyond)
    {
        var now = new DateTimeOffset(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(beyond, OfflineGrace.Beyond(now.AddDays(-daysAgo), now));
    }
}
