using System.Windows.Controls;

namespace ScreenTail.UI.Review;

/// <summary>
/// Layout only. Everything it shows is <see cref="ReviewViewModel"/>'s. A note's frame chip reaches the
/// filmstrip through the note's own ShowFrameCommand (T-165), so nothing here carries it.
/// </summary>
public partial class ReviewPane : UserControl
{
    public ReviewPane()
    {
        InitializeComponent();
    }
}
