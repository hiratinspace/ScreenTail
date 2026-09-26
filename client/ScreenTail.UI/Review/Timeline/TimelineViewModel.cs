using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenTail.Core.Review;
using ScreenTail.Shared.Schema;

namespace ScreenTail.UI.Review.Timeline;

/// <summary>One mark on the track, with what the view needs to draw it and what to say when hovered.</summary>
/// <param name="Position">Where it starts, as a fraction of the session.</param>
/// <param name="Span">How much of the session a band covers; zero for a point.</param>
/// <param name="FrameId">What clicking it selects. Only a frame marker has one.</param>
public sealed record MarkerItem(TimelineMarkerKind Kind, double Position, double Span, string Tooltip, string? FrameId)
{
    public bool IsBand => Span > 0;
}

/// <summary>
/// Review's bottom panel (ST-076, Spec §5 S3), bound over <see cref="SessionTimeline"/>, which decides
/// everything: what the markers are, where they sit, which frame a sentence points at. This only turns
/// that into things a control can bind to and two actions into commands.
///
/// Clicking a line selects its frame in the filmstrip, through the delegate the Review screen supplies;
/// a line with no frame selects nothing, because the model already said there is nothing honest to show.
/// </summary>
public sealed partial class TimelineViewModel : ObservableObject
{
    private readonly SessionTimeline _timeline;
    private readonly TimelinePanelState _state;
    private readonly Action<string?> _select;
    private readonly Action<bool>? _toggled;

    /// <param name="select">Selects a frame in the filmstrip, or clears the selection for null.</param>
    /// <param name="expanded">The remembered state (Spec §5 S3: "state persists").</param>
    /// <param name="toggled">Told each time the panel opens or closes, so the choice can be saved.</param>
    public TimelineViewModel(Session session, Action<string?> select, bool expanded = false, Action<bool>? toggled = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _select = select ?? throw new ArgumentNullException(nameof(select));
        _toggled = toggled;
        _timeline = new SessionTimeline(session);
        _state = new TimelinePanelState(expanded);
        Markers = [.. _timeline.Markers.Select(Item)];
        Lines = _timeline.Lines;
        Summary = Describe();
        Apply();
    }

    public IReadOnlyList<MarkerItem> Markers { get; }

    public IReadOnlyList<TranscriptLine> Lines { get; }

    /// <summary>"3 screenshots · 2 lines · 1 gap", beside the toggle, so a collapsed panel still says what is in it.</summary>
    public string Summary { get; }

    [ObservableProperty]
    public partial bool Expanded { get; set; }

    [ObservableProperty]
    public partial double Height { get; set; }

    [ObservableProperty]
    public partial string ToggleLabel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Visibility TranscriptVisibility { get; set; } = Visibility.Collapsed;

    [ObservableProperty]
    public partial TranscriptLine? SelectedLine { get; set; }

    /// <summary>`Alt+T`, and the header button.</summary>
    [RelayCommand]
    private void Toggle()
    {
        var expanded = _state.Toggle();
        Apply();
        _toggled?.Invoke(expanded);
    }

    /// <summary>A frame square on the track: the same jump a transcript line makes.</summary>
    [RelayCommand]
    private void JumpTo(string? frameId)
    {
        if (frameId is not null)
        {
            _select(frameId);
        }
    }

    partial void OnSelectedLineChanged(TranscriptLine? value)
    {
        if (value is not null)
        {
            _select(_timeline.FrameFor(value.Id));
        }
    }

    private void Apply()
    {
        Expanded = _state.Expanded;
        Height = _state.Height;
        ToggleLabel = _state.Expanded ? "▾ Timeline & transcript" : "▸ Timeline & transcript";
        TranscriptVisibility = _state.Expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private MarkerItem Item(TimelineMarker marker)
    {
        var span = marker.IsBand ? Math.Clamp((marker.EndMs - marker.TsMs) / (double)_timeline.DurationMs, 0, 1 - marker.Position) : 0;
        var when = marker.IsBand ? $"{Clock(marker.TsMs)}–{Clock(marker.EndMs)}" : Clock(marker.TsMs);
        var what = marker.Description ?? marker.Kind switch
        {
            TimelineMarkerKind.Click => "Click",
            TimelineMarkerKind.Frame => "Screenshot",
            TimelineMarkerKind.Marked => "Marked moment",
            TimelineMarkerKind.Narration => "Said with no screenshot near it",
            _ => marker.Kind.ToString(),
        };
        return new MarkerItem(marker.Kind, marker.Position, span, $"{when} · {what}", marker.FrameId);
    }

    private string Describe()
    {
        var frames = _timeline.Markers.Count(m => m.Kind == TimelineMarkerKind.Frame);
        var gaps = _timeline.Markers.Count(m => m.IsBand);
        var parts = new List<string> { Plural(frames, "screenshot"), Plural(Lines.Count, "line") };
        if (gaps > 0)
        {
            parts.Add(Plural(gaps, "gap"));
        }

        return string.Join(" · ", parts);
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Clock(long ms) => TimeSpan.FromMilliseconds(ms).ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
