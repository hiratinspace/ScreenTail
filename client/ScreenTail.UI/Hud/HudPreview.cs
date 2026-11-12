using System.IO;
using System.Windows;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Hud;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Render;

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
    /// <summary>Every variant the spec names, in the order it names them. The accessibility suite opens each too (ST-084).</summary>
    internal static IEnumerable<(string Name, HudViewModel Model, HudTone Tone)> Variants()
    {
        yield return ("recording", For(CaptureStates.Recording, elapsedMs: 761_000), HudTone.Recording);
        yield return ("paused-user", For(CaptureStates.Paused, reason: CaptureReasons.User), HudTone.Paused);
        yield return ("suppressed-password", For(CaptureStates.Suppressed, reason: CaptureReasons.PasswordField), HudTone.Paused);
        yield return ("suppressed-excluded", For(CaptureStates.Suppressed, reason: CaptureReasons.ExcludedApp), HudTone.Paused);
        yield return ("suppressed-elevated", For(CaptureStates.Suppressed, reason: CaptureReasons.ElevatedWindow), HudTone.Paused);
        yield return ("out-of-scope", For(CaptureStates.Suppressed, reason: CaptureReasons.OutOfScope, process: "OUTLOOK"), HudTone.Scope);
        yield return ("no-mic", For(CaptureStates.Recording, elapsedMs: 92_000, microphone: CapabilityState.Blocked), HudTone.Recording);
        yield return ("offline", For(CaptureStates.Recording, elapsedMs: 92_000, online: false), HudTone.Recording);

        // Not a spec variant, but the one that matters most: the UI has lost the service and must not
        // claim capture has stopped.
        yield return ("unknown", For(state: null), HudTone.Idle);

        // Redaction backlog, which shares the pill with everything else and is the piece most likely to
        // run into its neighbours.
        yield return ("redacting", For(CaptureStates.Recording, elapsedMs: 761_000, pending: 3), HudTone.Recording);
    }

    public static async Task CaptureAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        BindingErrors.Listen();

        foreach (var (name, model, tone) in Variants().Concat(Extras()))
        {
            var window = new HudWindow { DataContext = model, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = -4000 };
            window.Show();

            foreach (var theme in RenderThemes.All)
            {
                RenderThemeApplier.Apply(theme, Application.Current.Resources);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
                window.UpdateLayout();
                // The colour the state is supposed to be, taken off the pixels. Counting distinct
                // colours says the pill drew something; only this says it drew the right state.
                Console.WriteLine($"  {name}/{theme}: tone={tone} glyph foreground={window.GlyphForegroundDescription} size={window.ActualWidth:0}x{window.ActualHeight:0}");
                WindowRenderer.SaveExpecting(
                    window,
                    Path.Combine(directory, RenderThemes.FileName($"hud-{name}", theme)),
                    TokenColour(tone),
                    Colour("Brush.shell.bg"),
                    $"{tone} state");
            }

            window.Close();
        }

        var errors = BindingErrors.Collected;
        foreach (var error in errors)
        {
            Console.Error.WriteLine(error);
        }

        Console.WriteLine($"{errors.Count} binding errors");
        RenderIndex.Write(directory);
    }

    /// <summary>
    /// Renders beyond the spec's list of states, which the accessibility suite walks: the offline and
    /// no-mic chips together, and the pill clicked open, recording with three events and paused with the
    /// session just started (Spec §5 S2, v0.6.0).
    /// </summary>
    private static IEnumerable<(string Name, HudViewModel Model, HudTone Tone)> Extras()
    {
        yield return ("offline-no-mic", For(CaptureStates.Recording, elapsedMs: 761_000, microphone: CapabilityState.Blocked, online: false), HudTone.Recording);
        yield return ("expanded", Expanded(), HudTone.Recording);

        var paused = For(CaptureStates.Paused, elapsedMs: 95_000, reason: CaptureReasons.User);
        paused.Expanded = true;
        yield return ("expanded-paused", paused, HudTone.Paused);
    }

    /// <summary>
    /// The pill clicked open: the session card with its last three events and Mark moment. The events come
    /// the way they do in the app, from the service's snapshots and an accepted mark, so the render shows
    /// what a technician would see rather than lines typed in for it. Types and times only (INV-10).
    /// </summary>
    private static HudViewModel Expanded()
    {
        var time = new PreviewTime();
        var (shell, model) = Session(time, Capture(CaptureStates.Recording, elapsedMs: 0));
        model.Expanded = true;

        time.At(14, 12, 40);
        shell.Observe(Capture(CaptureStates.Recording, elapsedMs: 640_000, pending: 1));
        time.At(14, 12, 52);
        shell.Observe(Capture(CaptureStates.Recording, elapsedMs: 652_000));
        time.At(14, 13, 20);
        model.MomentMarked();
        time.At(14, 13, 51);
        shell.Observe(Capture(CaptureStates.Recording, elapsedMs: 711_000, pending: 2));
        time.At(14, 14, 3);
        shell.Observe(Capture(CaptureStates.Recording, elapsedMs: 761_000, pending: 1));
        return model;
    }

    /// <summary>The design token each tone must actually appear in, read from the live dictionary.</summary>
    private static (byte R, byte G, byte B) TokenColour(HudTone tone)
    {
        // The frame's state colours (Spec §2.1, shell.*): the pill is part of the frame since v0.6.0.
        var key = tone switch
        {
            HudTone.Recording => "Brush.shell.recording",
            HudTone.Paused => "Brush.shell.paused",
            HudTone.Scope => "Brush.shell.scope",
            _ => "Brush.shell.muted",
        };

        // Looked up rather than hard-coded, so darkening a token for contrast does not fail this check.
        return Colour(key);
    }

    private static (byte R, byte G, byte B) Colour(string key)
    {
        var brush = (System.Windows.Media.SolidColorBrush)Application.Current.Resources[key];
        return (brush.Color.R, brush.Color.G, brush.Color.B);
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
        var capture = state is null ? null : Capture(state, elapsedMs, reason, process, pending);
        return Session(new PreviewTime(), capture, microphone, online).Model;
    }

    private static CaptureStateSnapshot Capture(string state, long? elapsedMs = null, string? reason = null, string? process = null, int pending = 0) => new()
    {
        State = state,
        SessionId = "preview",
        ElapsedMs = elapsedMs,
        Reason = reason,
        ScopeProcess = process,
        PendingRedactions = pending,
        RemoteTool = "screenconnect",
    };

    private static (ShellState Shell, HudViewModel Model) Session(
        PreviewTime time,
        CaptureStateSnapshot? capture,
        CapabilityState microphone = CapabilityState.Ok,
        bool online = true)
    {
        var shell = new ShellState();
        if (capture is not null)
        {
            shell.Connected(capture);
        }

        // A clock that stands still, so each screenshot says the time it was given rather than however long
        // the render took after it (the pill's clock moves on its own since the owner's 2026-11-05 report).
        var model = new HudViewModel(shell, new RecordingClock(shell, StillTime.Instance), time)
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
        return (shell, model);
    }

    /// <summary>
    /// The wall clock the expanded pill stamps its events with: a fixed afternoon in UTC, so every render
    /// says the same times on every runner. The session starts at 14:02.
    /// </summary>
    private sealed class PreviewTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 12, 14, 2, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void At(int hour, int minute, int second) => _now = new DateTimeOffset(2026, 11, 12, hour, minute, second, TimeSpan.Zero);
    }

    /// <summary>A clock that never moves and whose timers never fire: a screenshot of one instant.</summary>
    private sealed class StillTime : TimeProvider
    {
        public static readonly StillTime Instance = new();

        public override long GetTimestamp() => 0;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => Never.Instance;

        private sealed class Never : ITimer
        {
            public static readonly Never Instance = new();

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
