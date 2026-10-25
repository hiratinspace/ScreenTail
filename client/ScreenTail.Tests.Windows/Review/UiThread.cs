using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenTail.Tests.Windows.Review;

/// <summary>
/// Runs a test body on its own STA thread with its own dispatcher, which WPF controls, bindings and input
/// routing require.
///
/// Nothing here touches <see cref="Application"/> or shows a <see cref="Window"/>. WPF allows one
/// Application per process for its whole life, and <c>PrivacyViewModelTests</c> makes it and shuts it
/// down; a window shown after that may never get a presentation source, which would make these tests
/// depend on the order the assembly runs in. A routed event raised on a control travels its tree the same
/// way with or without a window. An items control is different: a control that belongs to no presentation
/// source is never visible, and a virtualizing panel makes no item containers for a control that is not
/// visible, so <see cref="Hosted"/> gives it an <see cref="HwndSource"/> of its own, which needs no
/// Application and is never shown.
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

    /// <summary>Measures and arranges the control at a window's size, so an items control generates its containers.</summary>
    public static T Laid<T>(T element)
        where T : FrameworkElement
    {
        element.Measure(new Size(640, 480));
        element.Arrange(new Rect(0, 0, 640, 480));
        element.UpdateLayout();
        return element;
    }

    /// <summary>
    /// Makes the element the root of a hidden window of a window's size and lays it out, so it is visible to
    /// WPF and an items control generates its containers. Dispose the source before the test body ends.
    /// </summary>
    public static HwndSource Hosted(FrameworkElement element)
    {
        // WS_POPUP without WS_VISIBLE: a real presentation source that never appears or takes focus.
        var parameters = new HwndSourceParameters("review ui test", 640, 480) { WindowStyle = unchecked((int)0x80000000) };
        var source = new HwndSource(parameters) { RootVisual = element };
        element.UpdateLayout();
        return source;
    }

    /// <summary>
    /// What one keypress does to an element, the way the input manager does it: the tunnelling preview
    /// event first, then the bubbling one unless the preview was handled, in which case WPF marks the
    /// bubbling one handled too and no class or instance handler sees it. Class handlers run on both, as
    /// they do for a real key, so a ListBox or a TextBox still takes the keys it takes.
    /// </summary>
    public static bool Press(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? Detached.Source;
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

    /// <summary>
    /// The presentation source a key event names when the element has none. It is attached to nothing: routing follows the
    /// element tree the event is raised on, not the source, so no window or Application is involved.
    /// </summary>
    private sealed class Detached : PresentationSource
    {
        [ThreadStatic]
        private static Detached? _source;

        private Visual? _root;

        public static Detached Source => _source ??= new Detached();

        public override Visual? RootVisual
        {
            get => _root;
            set => _root = value;
        }

        public override bool IsDisposed => false;

        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
