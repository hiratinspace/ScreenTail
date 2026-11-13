using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ScreenTail.UI.Controls;

/// <summary>
/// An icon-font glyph that is decoration beside words saying the same thing (Spec §7: never the only
/// signal). Hidden from UI Automation: a plain TextBlock holding one is read by a screen reader as a
/// private-use character, which Narrator says as nothing or as a code point, and which Accessibility
/// Insights fails (ST-084). Also used for a mark that is not an icon but is decoration in the same way: a
/// step's number in the setup wizard's list, where the row's spoken name already says "Step 3 of 7".
/// </summary>
public sealed class Glyph : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => null!;
}
