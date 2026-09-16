using System.Runtime.Versioning;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Hud;

namespace ScreenTail.UI.Shell;

/// <summary>
/// Keeps one pill alive for the life of the application and shows or hides it (ST-085).
///
/// A holder rather than creating a window each time, because the pill remembers where the technician
/// dragged it (Spec §5 S2) and a new window each session would put it back in the corner. Hiding is
/// <see cref="System.Windows.Window.Hide"/>, not close, for the same reason.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HudWindowHolder
{
    private readonly HudWindow _window;
    private bool _shown;

    public HudWindowHolder(ShellState state, CaptureConnection connection, ShellPreferencesStore preferences)
    {
        _window = new HudWindow(preferences);
        var model = new HudViewModel(state);
        model.MarkRequested += () => _ = connection.SendAsync(id => new MarkMomentCommand { RequestId = id });
        _window.DataContext = model;
    }

    public void SetVisible(bool visible)
    {
        if (visible == _shown)
        {
            return;
        }

        _shown = visible;
        if (visible)
        {
            _window.Show();
        }
        else
        {
            _window.Hide();
        }
    }

    public void Close() => _window.Close();
}
