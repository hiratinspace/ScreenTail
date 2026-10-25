using System.Runtime.Versioning;
using ScreenTail.Core.Ipc;
using ScreenTail.Core.Shell;
using ScreenTail.Shared.Ipc;
using ScreenTail.UI.Shell;

namespace ScreenTail.Tests.Windows.Shell;

/// <summary>
/// Quitting takes the pill, the tray icon and the pipe down, in that order, on the thread that asked
/// (ST-085; 2026-10-12 review, S-47, E-27, T-157).
///
/// Every Quit used to end in a crash report: the pill was closed after the connection's dispose had moved
/// the rest of the work onto the thread pool, closing it there threw, and the icon was never disposed, a
/// ghost in the notification area. These hold the real shell to the fixed order, connected to a service
/// that is recording, the way App.Dispose calls it: on the UI thread, blocking.
///
/// The shell needs a UI thread and no WPF Application, so these run on an STA thread of their own. After
/// another test's Application has shut down, WPF will not show a window in this process again, so the
/// pill may never have been on screen here; closing it is the same either way, and so is what is held.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LiveShellQuitTests
{
    [Fact]
    public void QuittingWhileConnectedClosesThePillAndTheIconWithoutThrowing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        StaThread.Run(async () =>
        {
            var service = new RecordingService();
            var file = Onboarded();
            try
            {
                var shell = new LiveShell((_, _) => Task.FromResult<ICaptureChannel>(service), new ShellPreferencesStore(file));
                await shell.StartAsync();
                Assert.True(
                    await StaThread.UntilAsync(() => shell.State.Snapshot.Connection == ServiceConnection.Connected && shell.Pill is not null, TimeSpan.FromSeconds(10)),
                    "the shell never connected to the fake service or never built the pill");
                var pill = shell.Pill!;
                var closed = false;
                pill.Closed += (_, _) => closed = true;

                // As App.Dispose does it: synchronously, on the UI thread.
                shell.DisposeAsync().AsTask().GetAwaiter().GetResult();

                Assert.True(closed, "the pill was not closed");
                Assert.True(shell.Tray.Disposed, "the tray icon was not disposed");
                Assert.True(service.Disposed, "the pipe was left open");
                Assert.Null(shell.Pill);
            }
            finally
            {
                File.Delete(file);
            }
        });
    }

    [Fact]
    public void ThePipeIsClosedEvenWhenClosingThePillThrows()
    {
        // The pill belongs to its UI thread, and closing it from any other throws, which is how every
        // Quit used to fail. Whatever the pill does, the icon and the pipe still go, and the failure is
        // still raised rather than swallowed.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        StaThread.Run(async () =>
        {
            var service = new RecordingService();
            var file = Onboarded();
            try
            {
                var shell = new LiveShell((_, _) => Task.FromResult<ICaptureChannel>(service), new ShellPreferencesStore(file));
                await shell.StartAsync();
                Assert.True(
                    await StaThread.UntilAsync(() => shell.State.Snapshot.Connection == ServiceConnection.Connected && shell.Pill is not null, TimeSpan.FromSeconds(10)),
                    "the shell never connected to the fake service or never built the pill");
                var pill = shell.Pill!;

                var failed = await Record.ExceptionAsync(() => Task.Run(async () => await shell.DisposeAsync(), TestContext.Current.CancellationToken));

                Assert.NotNull(failed);
                Assert.True(service.Disposed, "closing the pill threw and the pipe was left open");
                Assert.True(shell.Tray.Disposed, "closing the pill threw and the tray icon was left behind");

                // The pill is still this thread's to close.
                pill.Close();
            }
            finally
            {
                File.Delete(file);
            }
        });
    }

    private static string Onboarded()
    {
        var file = Path.Combine(Path.GetTempPath(), $"screentail-shell-{Guid.NewGuid():N}.json");
        Assert.True(new ShellPreferencesStore(file).Save(new ShellPreferences { OnboardedAt = DateTimeOffset.UnixEpoch }), "the test could not write its preferences file");
        return file;
    }

    /// <summary>A service that is recording, answers every command, and has nothing to say to a question.</summary>
    private sealed class RecordingService : ICaptureChannel
    {
        private volatile bool _disposed;

        public CaptureStateSnapshot State { get; } = new() { State = CaptureStates.Recording, SessionId = "s1", ElapsedMs = 1_000 };

        public bool IsConnected => !_disposed;

        public bool Disposed => _disposed;

        public event Action<CaptureStateSnapshot>? StateChanged
        {
            add { }
            remove { }
        }

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public Task<CommandResult> SendAsync(Func<int, IpcCommand> build, CancellationToken ct = default) =>
            Task.FromResult(new CommandResult { RequestId = 1, Ok = true });

        public Task<TReply> RequestAsync<TReply>(Func<int, IpcCommand> build, CancellationToken ct = default)
            where TReply : IpcEvent =>
            Task.FromException<TReply>(new IOException("Not under test."));

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
