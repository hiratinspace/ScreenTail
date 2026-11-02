using System.Runtime.Versioning;
using Xunit.Sdk;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// The shared application's timeout (ST-084 review). A body that overruns fails, later calls fail at once
/// while it still holds the UI thread, and once it finishes the tests after it run again: a body that was
/// only slow must not fail the rest of the collection.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(WpfApplicationOwner.Name)]
public sealed class TheApplicationTests(TheApplication app)
{
    [Fact]
    public async Task ABodyThatOverrunsThenFinishesFreesTheTestsAfterIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var limit = app.Limit;
        app.Limit = TimeSpan.FromMilliseconds(300);
        try
        {
            var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = await Assert.ThrowsAsync<FailException>(() => app.RunAsync(() => slow.Task));

            // While it still runs, the next call fails at once rather than queueing behind it.
            var refused = await Assert.ThrowsAsync<FailException>(() => app.RunAsync(() => { }));
            Assert.Contains("did not finish", refused.Message, StringComparison.Ordinal);

            // It finishes, late: the thread is free and the next call runs.
            slow.SetResult();
            var until = DateTime.UtcNow.AddSeconds(10);
            var ran = false;
            while (!ran && DateTime.UtcNow < until)
            {
                try
                {
                    await app.RunAsync(() => ran = true);
                }
                catch (FailException)
                {
                    await Task.Delay(50, TestContext.Current.CancellationToken);
                }
            }

            Assert.True(ran, "A body that finished late still fails every call after it.");
        }
        finally
        {
            app.Limit = limit;
        }
    }
}
