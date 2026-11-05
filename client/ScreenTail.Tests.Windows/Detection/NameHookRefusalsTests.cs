using System.Runtime.Versioning;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Shell;
using ScreenTail.Service.Detection;
using ScreenTail.Shared.Ipc;
using ScreenTail.Tests.Windows.Performance;
using ScreenTail.UI.Diagnostics;

namespace ScreenTail.Tests.Windows.Detection;

/// <summary>
/// ST-031: when Windows refuses the foreground watcher's title-change hook for the thread in front, the
/// watcher listens to every thread instead and counts the refusal, and that count is what the "What's
/// being captured right now?" panel shows as "Hook fallbacks" (contract v8).
///
/// Windows does not refuse a hook on demand, so the watcher is told to answer the front window's thread
/// as Windows answers a refusal. Everything after that is the real thing: the watcher's own count, the
/// service's report, the pipe's framing, the panel and the window's view model with its Copy button.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class NameHookRefusalsTests
{
    [Fact]
    public async Task ARefusedNameHookIsCountedAndReachesTheDiagnosticsPanel()
    {
        Hardware.RequireDesktop();
        var ct = TestContext.Current.CancellationToken;
        var title = $"ScreenTail refused {Guid.NewGuid():N}";
        using var front = DesktopWindow.CreateOrdinary(title);
        front.RequireForeground();
        var refused = IdleHookTests.ThreadOf(front.Handle);

        // The poll out of reach, so the only refusals are the ones the foreground itself causes.
        await using var watcher = new WindowsForegroundWatcher(time: null, pollInterval: TimeSpan.FromMinutes(10), refuseNameHookFor: thread => thread == refused);

        // Before it starts, the watcher cannot know, and says so rather than 0.
        Assert.Null(watcher.NameHookRefusals);
        await watcher.StartAsync(ct);

        var refusals = watcher.NameHookRefusals;
        Assert.True(refusals >= 1, $"the watcher counted {refusals} refusals with the front window's thread refused");

        // The fallback is every thread, never the poll alone.
        Assert.True(watcher.UsingHook, "the foreground watcher's hooks were not installed");
        Assert.Equal(0u, watcher.NameHookThread);

        // The service's report, framed as the pipe frames it, read back as the UI reads it.
        var reported = ServiceDiagnostics.Report(
            serviceVersion: "0.0.1-test",
            scope: null,
            microphone: null,
            CaptureStateSnapshot.Idle,
            isHeld: _ => false,
            redactionBacklog: 0,
            redaction: null,
            framesRefused: 0,
            keystrokesDropped: 0,
            egressBlocked: 0,
            nameHookRefusals: watcher.NameHookRefusals,
            localOnly: true,
            policyVersion: "default",
            workingSetBytes: 0);
        using var pipe = new MemoryStream();
        await IpcFraming.WriteAsync<IpcEvent>(pipe, reported, ct);
        pipe.Position = 0;
        var read = Assert.IsType<DiagnosticsReported>(await IpcFraming.ReadAsync<IpcEvent>(pipe, ct));
        Assert.Equal(refusals, read.NameHookRefusals);

        string? copied = null;
        var view = new DiagnosticsViewModel(DiagnosticsPanel.From(Diagnostics.From(read)), copy: text => copied = text);
        var row = view.Rows.Single(r => r.Label == DiagnosticsPanel.HookFallbacks);
        Assert.Equal($"{refusals} ({DiagnosticsPanel.HookFallbacksMeaning})", row.Value);

        view.CopyCommand.Execute(null);
        Assert.NotNull(copied);
        Assert.Contains($"hook fallbacks   {refusals}", copied, StringComparison.Ordinal);

        // A count and nothing about the window (INV-10): its title appears nowhere the panel can show.
        Assert.DoesNotContain(title, copied, StringComparison.Ordinal);
        Assert.DoesNotContain(view.Rows, r => r.Value.Contains(title, StringComparison.Ordinal));
    }
}
