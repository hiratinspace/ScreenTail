using System.Windows;
using System.Windows.Controls.Primitives;

namespace ScreenTail.UI.Controls;

/// <summary>
/// Spec §3 Toggle. <see cref="IsLocked"/> is the admin-enforced variant (INV-11): shown with a lock,
/// tooltip "Set by your admin", and not toggleable. Template in Theme/Components.xaml.
/// </summary>
public sealed class ToggleSwitch : ToggleButton
{
    public static readonly DependencyProperty IsLockedProperty = DependencyProperty.Register(
        nameof(IsLocked),
        typeof(bool),
        typeof(ToggleSwitch),
        new PropertyMetadata(false, OnIsLockedChanged));

    static ToggleSwitch()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(ToggleSwitch), new FrameworkPropertyMetadata(typeof(ToggleSwitch)));
    }

    public bool IsLocked
    {
        get => (bool)GetValue(IsLockedProperty);
        set => SetValue(IsLockedProperty, value);
    }

    protected override void OnToggle()
    {
        if (!IsLocked)
        {
            base.OnToggle();
        }
    }

    private static void OnIsLockedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToggleSwitch toggle && (bool)e.NewValue)
        {
            toggle.ToolTip ??= "Set by your admin";
        }
    }
}
