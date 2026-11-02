using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ScreenTail.UI.Controls;

/// <summary>
/// A live region that is announced (ST-084). Use <c>controls:LiveRegion.Setting="Polite"</c> (or
/// Assertive) where the views used to say <c>AutomationProperties.LiveSetting</c>.
///
/// WPF only declares a live region. It never raises UI Automation's LiveRegionChanged event itself, so a
/// screen reader is told nothing when a live region's words change, and the forty-odd live regions in
/// these views (the status badge, the banner, every inline validation message, the wizard's step title)
/// were silent. This sets the live setting and, while the element is loaded, raises the event when its
/// text, content or automation name changes, or when it appears.
///
/// An element whose words change too often to read out, like the pill's running clock, says when it is
/// worth announcing with <see cref="KeyProperty"/>: only a change of the key is announced.
/// </summary>
public static class LiveRegion
{
    /// <summary>The live setting, set on the element for UI Automation, and the announcing switched on with it.</summary>
    public static readonly DependencyProperty SettingProperty = DependencyProperty.RegisterAttached(
        "Setting",
        typeof(AutomationLiveSetting),
        typeof(LiveRegion),
        new PropertyMetadata(AutomationLiveSetting.Off, OnSettingChanged));

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

    /// <summary>
    /// Every announcement, whether or not a screen reader is listening: the accessibility suite's view of
    /// what Narrator would be told (ST-084). Raised on the element's thread.
    /// </summary>
    internal static event Action<UIElement>? Announced;

    public static AutomationLiveSetting GetSetting(DependencyObject element) =>
        element is null ? AutomationLiveSetting.Off : (AutomationLiveSetting)element.GetValue(SettingProperty);

    public static void SetSetting(DependencyObject element, AutomationLiveSetting value) => element?.SetValue(SettingProperty, value);

    public static object? GetKey(DependencyObject element) => element?.GetValue(KeyProperty);

    public static void SetKey(DependencyObject element, object? value) => element?.SetValue(KeyProperty, value);

    /// <summary>Raises LiveRegionChanged for <paramref name="element"/>, when it is on screen and a screen reader listens.</summary>
    public static void Announce(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!element.IsVisible)
        {
            return;
        }

        Announced?.Invoke(element);
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            UIElementAutomationPeer.CreatePeerForElement(element)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private static void OnSettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        AutomationProperties.SetLiveSetting(d, (AutomationLiveSetting)e.NewValue);
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;
        element.Unloaded -= OnUnloaded;
        if ((AutomationLiveSetting)e.NewValue == AutomationLiveSetting.Off)
        {
            Stop(element);
            return;
        }

        element.Loaded += OnLoaded;
        element.Unloaded += OnUnloaded;
        if (element.IsLoaded)
        {
            Start(element);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Start((FrameworkElement)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Stop((FrameworkElement)sender);

    private static void Start(FrameworkElement element)
    {
        if (element.GetValue(WatchedProperty) is null)
        {
            element.SetValue(WatchedProperty, new Watcher(element));
        }
    }

    private static void Stop(FrameworkElement element)
    {
        if (element.GetValue(WatchedProperty) is Watcher watcher)
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
                if (property.OwnerType.IsInstanceOfType(element) || property == AutomationProperties.NameProperty)
                {
                    var descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType());
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
