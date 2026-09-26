using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenTail.Core.Review;

namespace ScreenTail.UI.Review.Timeline;

/// <summary>
/// The scrubber (ST-076). Layout only: which markers exist and where they sit are
/// <see cref="TimelineViewModel"/>'s, from <see cref="SessionTimeline"/>; this turns fractions into
/// pixels each time the track is measured, and a click on a frame square into the model's command.
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
                Fill = Brush(marker.Kind == TimelineMarkerKind.OutOfScope ? "Brush.state.scope" : "Brush.state.paused"),
                Opacity = 0.45,
                RadiusX = 2,
                RadiusY = 2,
                ToolTip = marker.Tooltip,
            };
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

    private Rectangle Square(double x, MarkerItem marker, TimelineViewModel model)
    {
        var square = new Rectangle
        {
            Width = 8,
            Height = 8,
            Fill = Brush("Brush.accent.primary"),
            Cursor = Cursors.Hand,
        };
        square.MouseLeftButtonUp += (_, e) =>
        {
            model.JumpToCommand.Execute(marker.FrameId);
            e.Handled = true;
        };
        Canvas.SetLeft(square, x - 4);
        Canvas.SetTop(square, 5);
        return square;
    }

    private TextBlock Flag(double x)
    {
        var flag = new TextBlock
        {
            Text = "⚑",
            FontSize = 12,
            Foreground = Brush("Brush.accent.primary.hover"),
        };
        Canvas.SetLeft(flag, x - 3);
        Canvas.SetTop(flag, 0);
        return flag;
    }

    private Rectangle Tick(double x)
    {
        var tick = new Rectangle { Width = 2, Height = 5, Fill = Brush("Brush.text.secondary") };
        Canvas.SetLeft(tick, x - 1);
        Canvas.SetTop(tick, 13);
        return tick;
    }

    private Ellipse Dot(double x)
    {
        var dot = new Ellipse { Width = 4, Height = 4, Fill = Brush("Brush.text.muted") };
        Canvas.SetLeft(dot, x - 2);
        Canvas.SetTop(dot, 7);
        return dot;
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
}
