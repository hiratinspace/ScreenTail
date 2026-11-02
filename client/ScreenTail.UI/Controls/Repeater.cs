using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ScreenTail.UI.Controls;

/// <summary>
/// An items control used for layout: the steps of a note, a card's rows, the setup wizard's dots. What it
/// holds is read by a screen reader as what it is (a text box, a button, a line of text), not as a list.
///
/// A plain <see cref="ItemsControl"/> does two things here that are wrong for that (ST-084). It is a Tab
/// stop with nothing to focus, so Tab landed on an invisible container in every pane that had one. And
/// UI Automation exposes it as an unnamed list whose items are read as their view models' type names
/// ("ScreenTail.UI.Review.StepRow", "StepDot { Number = 3, ... }"), which Accessibility Insights fails
/// twice over. This one takes no focus, and its peer is a plain container that is neither a control nor
/// content, so a screen reader passes through it to its items' own controls. A real list a person moves
/// through, like the screenshots or the transcript, is a ListBox.
/// </summary>
public sealed class Repeater : ItemsControl
{
    static Repeater()
    {
        FocusableProperty.OverrideMetadata(typeof(Repeater), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(Repeater), new FrameworkPropertyMetadata(false));
    }

    // Not null: WPF gives an items control that answers null a list peer of its own anyway.
    protected override AutomationPeer OnCreateAutomationPeer() => new LayoutPeer(this);

    /// <summary>A container in the raw automation tree only; its children are the visual tree's controls.</summary>
    private sealed class LayoutPeer(Repeater owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override string GetClassNameCore() => nameof(Repeater);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    }
}
