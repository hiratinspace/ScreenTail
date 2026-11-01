using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScreenTail.UI.Controls;

/// <summary>
/// An items control used for layout: the steps of a note, a card's rows, the setup wizard's dots. What it
/// holds is read by a screen reader as what it is (a text box, a button, a line of text), not as a list.
///
/// A plain <see cref="ItemsControl"/> does two things here that are wrong for that (ST-084). It is a Tab
/// stop with nothing to focus, so Tab landed on an invisible container in every pane that had one. And
/// UI Automation exposes it as an unnamed list whose items are read as their view models' type names
/// ("ScreenTail.UI.Review.StepRow", "StepDot { Number = 3, ... }"), which Accessibility Insights fails
/// twice over. This one takes no focus and exposes no list; its items' controls are its parent's.
/// A real list a person moves through, like the screenshots or the transcript, is a ListBox.
/// </summary>
public sealed class Repeater : ItemsControl
{
    static Repeater()
    {
        FocusableProperty.OverrideMetadata(typeof(Repeater), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Repeater), new FrameworkPropertyMetadata(false));
    }

    /// <summary>No peer of its own: its items' controls appear in the automation tree where it sits.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => null!;
}
