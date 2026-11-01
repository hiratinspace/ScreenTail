using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenTail.UI.Settings.Capture;

namespace ScreenTail.Tests.Windows.Settings;

/// <summary>
/// When the microphone card counts as looked at (ST-080 AC3, review of #244): shown, scrolled into view,
/// its window shown, in front and not minimised, and the desktop not locked, switched away or asleep.
/// The live level is read for exactly as long as this says yes.
///
/// Hosted in a plain window with a scroller, as SettingsPane hosts the card, without the app's styles:
/// the process may make one WPF Application and the Privacy tests own it. Whether the window is in front
/// is passed in, because a test runner's desktop may refuse to activate a window.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CardWatchTests
{
    [Fact]
    public void TheCardIsLookedAtOnlyWhileShownInViewInFrontAndAwake()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        OnUiThread(async () =>
        {
            var said = new List<bool>();
            var card = new Border { Height = 100 };
            var scroller = new ScrollViewer { Height = 200, Content = new StackPanel { Children = { card, new Border { Height = 2000 } } } };
            var window = new Window { Width = 300, Height = 260, Content = scroller, ShowInTaskbar = false, ShowActivated = false, Left = 0, Top = 0 };
            var front = true;
            var watch = new CardWatch(card, said.Add) { InFront = _ => front };
            try
            {
                window.Show();
                await Settle();
                Assert.True(watch.Watched, "shown, in view and in front");
                Assert.True(watch.HoldsSystemEvents);

                front = false;
                watch.Recheck();
                Assert.False(watch.Watched, "behind another window");
                front = true;
                watch.Recheck();
                Assert.True(watch.Watched);

                window.WindowState = WindowState.Minimized;
                await Settle();
                Assert.False(watch.Watched, "minimised");
                window.WindowState = WindowState.Normal;
                await Settle();
                Assert.True(watch.Watched);

                scroller.ScrollToVerticalOffset(1000);
                await Settle();
                Assert.False(watch.Watched, "scrolled out of view");
                scroller.ScrollToVerticalOffset(0);
                await Settle();
                Assert.True(watch.Watched);

                card.Visibility = Visibility.Collapsed;
                await Settle();
                Assert.False(watch.Watched, "hidden");
                card.Visibility = Visibility.Visible;
                await Settle();
                Assert.True(watch.Watched);

                foreach (var away in new[] { SessionSwitchReason.SessionLock, SessionSwitchReason.ConsoleDisconnect, SessionSwitchReason.RemoteDisconnect })
                {
                    watch.SessionSwitched(away);
                    Assert.False(watch.Watched, away.ToString());

                    // A connect may land on the lock screen: only the unlock brings it back.
                    watch.SessionSwitched(SessionSwitchReason.ConsoleConnect);
                    Assert.False(watch.Watched, $"{away} then a connect");
                    watch.SessionSwitched(SessionSwitchReason.SessionUnlock);
                    Assert.True(watch.Watched, $"{away} then unlocked");
                }

                watch.PowerChanged(PowerModes.Suspend);
                Assert.False(watch.Watched, "asleep");
                watch.PowerChanged(PowerModes.Resume);
                Assert.True(watch.Watched, "awake");

                window.Hide();
                await Settle();
                Assert.False(watch.Watched, "window hidden");
                window.Show();
                await Settle();
                Assert.True(watch.Watched);
            }
            finally
            {
                window.Close();
                await Settle();
            }

            Assert.False(watch.Watched, "closed");
            Assert.False(watch.HoldsSystemEvents, "Windows' events given back when the card unloads");
            Assert.False(said[^1]);

            // Told once per change, never twice in a row the same.
            Assert.All(said.Zip(said.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
        });
    }

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.Background);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
    }

    private static void OnUiThread(Func<Task> body)
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
        { IsBackground = true, Name = "card watch test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The UI thread did not finish.");
        failure?.Throw();
    }
}
