using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// Runs a test body on an STA thread with a dispatcher, which WPF controls, bindings and input routing
/// require. No <see cref="Application"/> is made: WPF allows one per process for its whole life, and
/// <c>PrivacyViewModelTests</c> is the test that makes it, so a view that needs the app's styles cannot be
/// built here. Bare controls can, and so can a window to give them a presentation source.
/// </summary>
internal static class UiThread
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
        { IsBackground = true, Name = "review ui test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The UI thread did not finish.");
        failure?.Throw();
    }

    /// <summary>
    /// A real window off the edge of the desktop holding the control, so the control has a presentation
    /// source (a key event needs one) and a layout (an items control generates its containers in one).
    /// Not activated: these tests type nothing, they raise the routed events a keypress would.
    /// </summary>
    public static Window Shown(UIElement content)
    {
        var window = new Window
        {
            Content = content,
            Width = 640,
            Height = 480,
            Left = -4000,
            Top = -4000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    /// <summary>
    /// What one keypress does to an element, the way the input manager does it: the tunnelling preview
    /// event first, then the bubbling one unless the preview was handled, in which case WPF marks the
    /// bubbling one handled too and no class or instance handler sees it.
    /// </summary>
    public static bool Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("The element is not in a window.");
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (preview.Handled)
        {
            return true;
        }

        var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
        target.RaiseEvent(down);
        return down.Handled;
    }
}
