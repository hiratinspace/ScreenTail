using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;

namespace ScreenTail.UI.Controls;

/// <summary>
/// The page-up and page-down areas of a scroll bar or a slider's track: a repeat button, as a track needs,
/// that UI Automation does not show as a control (ST-084).
///
/// A screen reader scrolls a pane and moves a slider through their own Scroll and RangeValue patterns, and
/// never through these. Shown as buttons they were nameless, and the one beside a thumb at the end of its
/// track is a few pixels tall, which Accessibility Insights fails as an unreasonable size.
/// </summary>
public sealed class PageButton : RepeatButton
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PartPeer(this);

    /// <summary>In the raw automation tree only: neither a control nor content.</summary>
    private sealed class PartPeer(PageButton owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override string GetClassNameCore() => nameof(PageButton);
    }
}
