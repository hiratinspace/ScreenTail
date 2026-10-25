using System.ComponentModel;
using ScreenTail.UI.Hud;

namespace ScreenTail.UI.Shell;

/// <summary>
/// The pill's gestures, as the shell hears them (Spec §5 S2): double-click asks for Review of the current
/// session, right-click asks for the pill to be put away.
///
/// Apart from the window that draws the pill so they are tested without one (2026-10-12 review, T-372).
/// Until they were wired, only Mark moment reached anything: double-click and right-click worked in the
/// render harness and nowhere else.
/// </summary>
public sealed class HudGestures
{
    private readonly HudViewModel _model;
    private readonly Func<bool> _shellShowsPill;

    /// <param name="model">The pill's view model, whose commands the gestures are bound to.</param>
    /// <param name="shellShowsPill">Whether the shell, which owns the hidden flag, has the pill up.</param>
    public HudGestures(HudViewModel model, Func<bool> shellShowsPill)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _shellShowsPill = shellShowsPill ?? throw new ArgumentNullException(nameof(shellShowsPill));
        _model.ReviewRequested += () => ReviewRequested?.Invoke();
        _model.PropertyChanged += OnModelChanged;
    }

    /// <summary>The technician double-clicked the pill.</summary>
    public event Action? ReviewRequested;

    /// <summary>
    /// The technician right-clicked the pill away while capture is known to be off. Never raised while a
    /// session runs, because <see cref="Core.Hud.HudState"/> keeps the pill up then whatever was clicked.
    /// </summary>
    public event Action? HideRequested;

    /// <summary>
    /// The right-click sets the view model's own hidden flag and nothing else listened. The model saying
    /// the pill may go while the shell still has it up is that gesture; once the shell has put it away,
    /// later refreshes of the same wish say nothing new.
    /// </summary>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HudViewModel.Visible) && !_model.Visible && _shellShowsPill())
        {
            HideRequested?.Invoke();
        }
    }
}
