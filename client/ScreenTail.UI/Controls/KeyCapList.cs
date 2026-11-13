using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ScreenTail.UI.Controls;

/// <summary>
/// A shortcut sheet line's key caps (Spec §4, v0.6.11), drawn as caps and read as one line of text: its
/// automation name ("Alt+1, Alt+2 or Alt+3") with no children. Each cap read on its own was three or four
/// elements a line, the commas between them more, and the sheet's Accessibility Insights scan ran past its
/// two minutes on the hosted runner; a screen reader also heard the keys in pieces.
/// </summary>
public sealed class KeyCapList : ItemsControl
{
    static KeyCapList()
    {
        FocusableProperty.OverrideMetadata(typeof(KeyCapList), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(KeyCapList), new FrameworkPropertyMetadata(false));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LinePeer(this);

    /// <summary>One text element, named by the line's keys, with nothing under it.</summary>
    private sealed class LinePeer(KeyCapList owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(KeyCapList);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
