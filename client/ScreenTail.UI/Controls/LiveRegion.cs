using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ScreenTail.UI.Controls;

/// <summary>
/// Makes <c>AutomationProperties.LiveSetting</c> do what the XAML says it does (ST-084).
///
/// WPF only declares a live region. It never raises UI Automation's LiveRegionChanged event itself, so a
/// screen reader is told nothing when a live region's words change, and the forty-odd live regions in
/// these views (the status badge, the banner, every inline validation message, the wizard's step title)
/// were silent. This watches every loaded element whose LiveSetting is not Off and raises the event when
/// its text, content, automation name or visibility changes, which is when Narrator should read it.
///
/// An element whose words change too often to read out, like the pill's running clock, says when it is
/// worth announcing with <see cref="KeyProperty"/>: only a change of the key is announced.
/// </summary>
public static class LiveRegion
{
    /// <summary>When set, the element is announced when this value changes, and not when its words do.</summary>
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key",
        typeof(object),
        typeof(LiveRegion),
        new PropertyMetadata(null, OnKeyChanged));

    private static readonly DependencyProperty WatchedProperty = DependencyProperty.RegisterAttached(
        "Watched",
        typeof(Watcher),
        typeof(LiveRegion));

    private static readonly DependencyProperty[] Words =
    [
        TextBlock.TextProperty,
        ContentControl.ContentProperty,
        AutomationProperties.NameProperty,
    ];

    private static int _watching;

    public static object? GetKey(DependencyObject element) => element?.GetValue(KeyProperty);

    public static void SetKey(DependencyObject element, object? value) => element?.SetValue(KeyProperty, value);

    /// <summary>Starts watching, once per process. Called at start-up, before any window is built.</summary>
    public static void Watch()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1)
        {
            return;
        }

        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.UnloadedEvent, new RoutedEventHandler(OnUnloaded), handledEventsToo: true);
    }

    /// <summary>Raises LiveRegionChanged for <paramref name="element"/>, if a screen reader is listening and it is on screen.</summary>
    public static void Announce(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!element.IsVisible || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender)
            && sender is FrameworkElement element
            && AutomationProperties.GetLiveSetting(element) != AutomationLiveSetting.Off
            && element.GetValue(WatchedProperty) is null)
        {
            element.SetValue(WatchedProperty, new Watcher(element));
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender) && sender is FrameworkElement element && element.GetValue(WatchedProperty) is Watcher watcher)
        {
            watcher.Stop();
            element.ClearValue(WatchedProperty);
        }
    }

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element && d.GetValue(WatchedProperty) is not null && !Equals(e.OldValue, e.NewValue))
        {
            Announce(element);
        }
    }

    /// <summary>The change notifications one live region holds, removed when it unloads so nothing leaks.</summary>
    private sealed class Watcher
    {
        private readonly FrameworkElement _element;
        private readonly List<DependencyPropertyDescriptor> _descriptors = [];

        public Watcher(FrameworkElement element)
        {
            _element = element;
            foreach (var property in Words)
            {
                if (DependencyPropertyDescriptor.FromProperty(property, element.GetType()) is { } descriptor)
                {
                    descriptor.AddValueChanged(element, OnChanged);
                    _descriptors.Add(descriptor);
                }
            }

            // Shown is said too: a banner or an error appears by its container becoming visible, and its
            // own words may never change.
            element.IsVisibleChanged += OnShown;
        }

        public void Stop()
        {
            foreach (var descriptor in _descriptors)
            {
                descriptor.RemoveValueChanged(_element, OnChanged);
            }

            _descriptors.Clear();
            _element.IsVisibleChanged -= OnShown;
        }

        private void OnShown(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is true)
            {
                OnChanged(sender, EventArgs.Empty);
            }
        }

        private void OnChanged(object? sender, EventArgs e)
        {
            if (_element.ReadLocalValue(KeyProperty) == DependencyProperty.UnsetValue)
            {
                Announce(_element);
            }
        }
    }
}
