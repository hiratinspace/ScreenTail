using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using ScreenTail.Core.Review;

namespace ScreenTail.UI.Review.Timeline;

/// <summary>
/// The scrubber (ST-076). Layout only: which markers exist and where they sit are
/// <see cref="TimelineViewModel"/>'s, from <see cref="SessionTimeline"/>; this turns fractions into
/// pixels each time the track is measured, and a click on a frame square into the model's command.
///
/// The markers' colours are resource references, not brushes looked up once: drawn with the brush of the
/// palette at the time, they kept the dark colours when Windows switched to high contrast (ST-084).
/// </summary>
public partial class TimelineView : UserControl
{
    public TimelineView()
    {
        InitializeComponent();
        Loaded += (_, _) => Draw();
        DataContextChanged += (_, _) => Draw();
        Track.SizeChanged += (_, _) => Draw();
    }

    private TimelineViewModel? Model => DataContext as TimelineViewModel;

    private void Draw()
    {
        Track.Children.Clear();
        var width = Track.ActualWidth;
        if (Model is not { } model || width <= 0)
        {
            return;
        }

        // Bands first, so points draw over them: a frame inside a scope band is still a frame.
        foreach (var marker in model.Markers.Where(m => m.IsBand))
        {
            var band = new Rectangle
            {
                Width = Math.Max(2, marker.Span * width),
                Height = 12,
                Opacity = 0.45,
                RadiusX = 2,
                RadiusY = 2,
                ToolTip = marker.Tooltip,
            };
            band.SetResourceReference(Shape.FillProperty, marker.Kind == TimelineMarkerKind.OutOfScope ? "Brush.state.scope" : "Brush.state.paused");
            Canvas.SetLeft(band, marker.Position * width);
            Canvas.SetTop(band, 3);
            Track.Children.Add(band);
        }

        foreach (var marker in model.Markers.Where(m => !m.IsBand))
        {
            var x = marker.Position * width;
            FrameworkElement shape = marker.Kind switch
            {
                TimelineMarkerKind.Frame => Square(x, marker, model),
                TimelineMarkerKind.Marked => Flag(x),
                TimelineMarkerKind.Narration => Tick(x),
                _ => Dot(x),
            };
            shape.ToolTip = marker.Tooltip;
            Track.Children.Add(shape);
        }
    }

    private static Rectangle Square(double x, MarkerItem marker, TimelineViewModel model)
    {
        var square = new Rectangle
        {
            Width = 8,
            Height = 8,
            Cursor = Cursors.Hand,
        };
        square.SetResourceReference(Shape.FillProperty, "Brush.accent.primary");
        square.MouseLeftButtonUp += (_, e) =>
        {
            model.JumpToCommand.Execute(marker.FrameId);
            e.Handled = true;
        };
        Canvas.SetLeft(square, x - 4);
        Canvas.SetTop(square, 5);
        return square;
    }

    private static TextBlock Flag(double x)
    {
        var flag = new TextBlock
        {
            Text = "⚑",
            FontSize = 12,
        };
        flag.SetResourceReference(TextBlock.ForegroundProperty, "Brush.accent.primary.hover");
        Canvas.SetLeft(flag, x - 3);
        Canvas.SetTop(flag, 0);
        return flag;
    }

    private static Rectangle Tick(double x)
    {
        var tick = new Rectangle { Width = 2, Height = 5 };
        tick.SetResourceReference(Shape.FillProperty, "Brush.text.secondary");
        Canvas.SetLeft(tick, x - 1);
        Canvas.SetTop(tick, 13);
        return tick;
    }

    private static Ellipse Dot(double x)
    {
        var dot = new Ellipse { Width = 4, Height = 4 };
        dot.SetResourceReference(Shape.FillProperty, "Brush.text.muted");
        Canvas.SetLeft(dot, x - 2);
        Canvas.SetTop(dot, 7);
        return dot;
    }

}
