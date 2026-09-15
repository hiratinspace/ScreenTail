using System.IO;
using System.Windows;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Render;
using ScreenTail.UI.Theme;
using AppTheme = ScreenTail.UI.Theme.AppTheme;

namespace ScreenTail.UI.Hud;

/// <summary>
/// Renders the pill in every state Spec §5 S2 lists, in every theme (ST-072).
///
/// The ticket asks for screenshots of all variants, and this is the only way to get them while the
/// development machine is a Mac. It also checks the XAML: a missing resource key or a converter on the
/// wrong type is a runtime failure that the compiler does not see, and the note pane's first render
/// caught exactly that.
/// </summary>
internal static class HudPreview
{
    /// <summary>Every variant the spec names, in the order it names them.</summary>
    private static IEnumerable<(string Name, HudViewModel Model)> Variants()
    {
        yield return ("recording", For(CaptureStates.Recording, elapsedMs: 761_000));
        yield return ("paused-user", For(CaptureStates.Paused, reason: CaptureReasons.User));
        yield return ("suppressed-password", For(CaptureStates.Suppressed, reason: CaptureReasons.PasswordField));
        yield return ("suppressed-excluded", For(CaptureStates.Suppressed, reason: CaptureReasons.ExcludedApp));
        yield return ("suppressed-elevated", For(CaptureStates.Suppressed, reason: CaptureReasons.ElevatedWindow));
        yield return ("out-of-scope", For(CaptureStates.Suppressed, reason: CaptureReasons.OutOfScope, process: "OUTLOOK"));
        yield return ("no-mic", For(CaptureStates.Recording, elapsedMs: 92_000, microphone: CapabilityState.Blocked));
        yield return ("offline", For(CaptureStates.Recording, elapsedMs: 92_000, online: false));

        // Not a spec variant, but the one that matters most: the UI has lost the service and must not
        // claim capture has stopped.
        yield return ("unknown", For(state: null));

        // Redaction backlog, which shares the pill with everything else and is the piece most likely to
        // run into its neighbours.
        yield return ("redacting", For(CaptureStates.Recording, elapsedMs: 761_000, pending: 3));
    }

    public static async Task CaptureAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        BindingErrors.Listen();

        foreach (var (name, model) in Variants())
        {
            var window = new HudWindow { DataContext = model, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
            window.Show();

            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.HighContrast })
            {
                ThemeManager.Apply(theme, Application.Current.Resources);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                window.UpdateLayout();
                WindowRenderer.Save(window, Path.Combine(directory, $"hud-{name}-{theme.ToString().ToLowerInvariant()}.png"));
            }

            window.Close();
        }

        var errors = BindingErrors.Collected;
        foreach (var error in errors)
        {
            Console.Error.WriteLine(error);
        }

        Console.WriteLine($"{errors.Count} binding errors");
    }

    private static HudViewModel For(
        string? state,
        long? elapsedMs = null,
        string? reason = null,
        string? process = null,
        int pending = 0,
        CapabilityState microphone = CapabilityState.Ok,
        bool online = true)
    {
        var shell = new ShellState();
        if (state is not null)
        {
            shell.Connected(new CaptureStateSnapshot
            {
                State = state,
                SessionId = "preview",
                ElapsedMs = elapsedMs,
                Reason = reason,
                ScopeProcess = process,
                PendingRedactions = pending,
            });
        }

        return new HudViewModel(shell)
        {
            Online = online,
            Capabilities = new CapabilityReport(
                DateTimeOffset.UnixEpoch,
                [
                    CapabilityCopy.Ok(Capability.DesktopSession, "ok"),
                    microphone is CapabilityState.Ok
                        ? CapabilityCopy.Ok(Capability.Microphone, "ok")
                        : CapabilityCopy.NoMicrophone(),
                    CapabilityCopy.Ok(Capability.ScreenCapture, "ok"),
                    CapabilityCopy.Ok(Capability.InputHooks, "ok"),
                    CapabilityCopy.Ok(Capability.ElevatedWindows, "ok"),
                ]),
        };
    }
}
