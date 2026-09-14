using System.Windows;
using System.Windows.Controls;

namespace ScreenTail.UI.Review;

/// <summary>
/// Picks the screenshot template or the gap template for each cell in the strip (ST-075).
///
/// Both live in one <c>ItemsSource</c> because they are one sequence in time: a gap has to be drawn
/// <em>between</em> the frames either side of it, and two collections drawn side by side cannot say
/// which came first.
/// </summary>
public sealed class StripItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Frame { get; set; }

    public DataTemplate? Gap { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container) => item switch
    {
        FrameItem => Frame,
        GapItem => Gap,
        _ => base.SelectTemplate(item, container),
    };
}
