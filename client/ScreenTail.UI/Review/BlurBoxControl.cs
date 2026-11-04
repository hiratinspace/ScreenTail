using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ScreenTail.UI.Review;

/// <summary>
/// The blur box drawn over an enlarged screenshot, which takes keyboard focus so the arrows can place it
/// (ST-084 follow-up). Where it is and how the keys change it are <see cref="Core.Review.BlurBox"/>'s;
/// this is only something focusable that a screen reader can find.
///
/// A control of its own because nothing stock fits: a Thumb is read as a scroll bar's part and Accessibility
/// Insights checks it as one, and a panel has no automation peer at all, so a focused one is silent. It is
/// read as its name and a "blur box" role, and its help text says which keys move it.
/// </summary>
public sealed class BlurBoxControl : Control
{
    static BlurBoxControl()
    {
        FocusableProperty.OverrideMetadata(typeof(BlurBoxControl), new FrameworkPropertyMetadata(true));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new BlurBoxAutomationPeer(this);

    private sealed class BlurBoxAutomationPeer(BlurBoxControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

        /// <summary>Not "custom", which Accessibility Insights fails and Narrator reads as nothing useful.</summary>
        protected override string GetLocalizedControlTypeCore() => "blur box";

        protected override string GetClassNameCore() => nameof(BlurBoxControl);
    }
}
