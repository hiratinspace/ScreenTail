using System.Windows.Controls;

namespace ScreenTail.UI.Review.Publish;

/// <summary>Layout only. Every rule is <see cref="Core.Review.Publish.PublishPanel"/>'s, every binding <see cref="PublishViewModel"/>'s.</summary>
public partial class PublishPane : UserControl
{
    public PublishPane()
    {
        InitializeComponent();
    }
}
