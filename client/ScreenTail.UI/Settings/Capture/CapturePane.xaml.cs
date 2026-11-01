using System.Windows.Controls;

namespace ScreenTail.UI.Settings.Capture;

/// <summary>
/// Layout only. Every rule is <see cref="Core.Settings.CapturePanel"/>'s.
///
/// The one thing worked out on this side is whether the microphone card is being looked at
/// (<see cref="CardWatch"/>, ST-080 AC3), which is passed to the view model so the live level is read
/// for exactly as long as it is true.
/// </summary>
public partial class CapturePane : UserControl
{
    private readonly CardWatch _microphone;

    public CapturePane()
    {
        InitializeComponent();
        _microphone = new CardWatch(MicrophoneCard, watched =>
        {
            if (DataContext is CaptureViewModel model)
            {
                _ = model.SetMicrophoneCardWatchedAsync(watched);
            }
        });
        DataContextChanged += (_, e) =>
        {
            // The old view model is told it is no longer looked at; the new one hears the current answer.
            if (e.OldValue is CaptureViewModel old)
            {
                _ = old.SetMicrophoneCardWatchedAsync(false);
            }

            if (e.NewValue is CaptureViewModel model && _microphone.Watched)
            {
                _ = model.SetMicrophoneCardWatchedAsync(true);
            }
        };
    }
}
