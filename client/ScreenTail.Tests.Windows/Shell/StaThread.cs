using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>
/// Runs a test body on its own STA thread with its own dispatcher, which a WPF window requires.
///
/// No <see cref="System.Windows.Application"/>: WPF allows one per process, and PrivacyViewModelTests
/// makes it and shuts it down. A window needs only an STA thread. No XAML window can be built after that
/// shutdown, so PrivacyViewModelTests' collection runs after every other (<see cref="WpfApplicationOwner"/>).
/// </summary>
internal static class StaThread
{
    /// <param name="within">How long the body may take. A minute unless a test measures for longer.</param>
    public static void Run(Func<Task> body, TimeSpan? within = null)
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
        Assert.True(thread.Join(within ?? TimeSpan.FromSeconds(60)), "The UI thread did not finish.");
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
