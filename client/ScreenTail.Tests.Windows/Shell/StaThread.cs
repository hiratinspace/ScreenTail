using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>
/// Runs a test body on its own STA thread with its own dispatcher, which a WPF window requires.
///
/// No <see cref="System.Windows.Application"/>: WPF allows one per process, and PrivacyViewModelTests
/// makes it and shuts it down. A window needs only an STA thread. After that shutdown WPF will not show
/// a window again in this process (its Show returns without creating one), so a body that shows one must
/// hold either way; closing works on a window that was never shown, and raises Closed the same.
/// </summary>
internal static class StaThread
{
    public static void Run(Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            var task = body();
            _ = task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            if (task.Exception is { } thrown)
            {
                failure = ExceptionDispatchInfo.Capture(thrown.InnerException ?? thrown);
            }

            dispatcher.InvokeShutdown();
        })
        { IsBackground = true, Name = "shell ui test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The UI thread did not finish.");
        failure?.Throw();
    }

    public static void Run(Action body) => Run(() =>
    {
        body();
        return Task.CompletedTask;
    });

    /// <summary>Lets the dispatcher run until <paramref name="done"/> holds, or the time is up.</summary>
    public static async Task<bool> UntilAsync(Func<bool> done, TimeSpan within)
    {
        var until = DateTime.UtcNow + within;
        while (!done())
        {
            if (DateTime.UtcNow > until)
            {
                return false;
            }

            await Task.Delay(20);
        }

        return true;
    }
}
