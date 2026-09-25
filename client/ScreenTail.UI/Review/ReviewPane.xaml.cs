using System.Windows.Controls;

namespace ScreenTail.UI.Review;

/// <summary>
/// Layout only. Everything it shows is <see cref="ReviewViewModel"/>'s. The one handler here carries a
/// note's frame chip to the filmstrip, which until 2026-09-25 was raised and never heard (ST-075).
/// </summary>
public partial class ReviewPane : UserControl
{
    public ReviewPane()
    {
        InitializeComponent();
    }

    private void OnFrameRequested(object? sender, string frameId)
    {
        if (DataContext is ReviewViewModel review)
        {
            _ = review.Strip.Select(frameId);
        }
    }
}
