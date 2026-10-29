using System.Windows;
using System.Windows.Controls;

namespace ScreenTail.UI.Shell;

/// <summary>
/// Picks the empty-state template for the shell's content area when it holds a sentence rather than a
/// pane, and leaves every other choice to the usual lookup by type.
///
/// A keyless template for <see cref="string"/> in the window's resources did this until v0.5, and it also
/// applied to every button's label inside every pane, because a button shows its string content through
/// the same lookup. While that template was a plain text block nobody noticed; once it became a card with
/// an icon, every button in Review turned into one. Choosing here keeps it to the one place it is meant for.
/// </summary>
public sealed class ShellContentTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Message { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is string ? Message : base.SelectTemplate(item, container);
}
