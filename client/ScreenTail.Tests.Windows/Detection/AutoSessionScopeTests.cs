using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;
using ScreenTail.Core.Sessions;
using ScreenTail.Core.Store;
using ScreenTail.Service.Detection;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Windows.Detection;

/// <summary>
/// Which window the capture loops are pointed at (ST-023; found 2026-09-21 while reviewing for
/// efficiency).
///
/// <c>CurrentScope</c> is what the screenshot capturer asks before taking a frame, and it carries the
/// window handle the frame is expected to belong to. It was only refreshed when the scope or the tool
/// changed — so a technician moving between two windows of the same remote tool, which is what a second
/// customer session looks like, left the old handle in place.
///
/// <c>CaptureForegroundWindow(expected:)</c> then finds the foreground window is not the one it was told
/// to expect and returns nothing. No error, no interval on the timeline: the frames are simply missing
/// from the half of the session that happened in the second window.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class AutoSessionScopeTests : IAsyncDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly RemoteToolRegistry Shipped = RemoteToolRegistry.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));

    [Fact]
    public async Task TheSecondWindowOfTheSameToolIsTheOneCaptureFollows()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);

        await coordinator.HandleAsync(Window(handle: 111), ct);
        await coordinator.HandleAsync(Window(handle: 222), ct);

        Assert.Equal(222, coordinator.CurrentScope?.Window);
    }

    [Fact]
    public async Task TheFirstWindowIsStillWhatItSays()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct);

        await coordinator.HandleAsync(Window(handle: 111), ct);

        Assert.Equal(CaptureScope.RemoteTool, coordinator.CurrentScope?.Scope);
        Assert.Equal(111, coordinator.CurrentScope?.Window);
    }

    [Fact]
    public async Task ATicketInTheTitleIsRememberedAndTheClipboardIsLeftAlone()
    {
        // ST-077 AC1: "#48213" in the window → the session carries 48213 for Review to pre-select. The
        // title said enough, so the clipboard was never asked.
        var ct = TestContext.Current.CancellationToken;
        var reads = 0;
        var coordinator = await CoordinatorAsync(ct, clipboard: () => { reads++; return "99999"; });

        await coordinator.HandleAsync(Window(handle: 111, title: "Ticket #48213 - Printer offline - Remote Desktop"), ct);

        var session = (await _store!.LoadSessionAsync(_machine!.SessionId!, ct))!;
        Assert.Equal("48213", session.SuggestedTicket);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task WithNothingInTheTitleTheClipboardIsReadOnceAtStart()
    {
        // AC3: once, at start. A second foreground change in the same session reads nothing.
        var ct = TestContext.Current.CancellationToken;
        var reads = 0;
        var coordinator = await CoordinatorAsync(ct, clipboard: () => { reads++; return "50011"; });

        await coordinator.HandleAsync(Window(handle: 111), ct);
        await coordinator.HandleAsync(Window(handle: 222), ct);

        var session = (await _store!.LoadSessionAsync(_machine!.SessionId!, ct))!;
        Assert.Equal("50011", session.SuggestedTicket);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task NoTicketAnywhereMeansNoSuggestion()
    {
        // AC2: no match → the picker is as before. Nothing invented from a clipboard full of other things.
        var ct = TestContext.Current.CancellationToken;
        var coordinator = await CoordinatorAsync(ct, clipboard: () => "call 0161 496 0123");

        await coordinator.HandleAsync(Window(handle: 111), ct);

        Assert.Null((await _store!.LoadSessionAsync(_machine!.SessionId!, ct))!.SuggestedTicket);
    }

    private async Task<AutoSessionCoordinator> CoordinatorAsync(CancellationToken ct, Func<string?>? clipboard = null)
    {
        _store = await SqliteSessionStore.OpenAsync(_path, new FixedKey(RandomNumberGenerator.GetBytes(32)), ct: ct);
        _machine = new SessionMachine(_store, new NoCaptureSources(), new UnavailableDrafter());
        var policy = new ScopePolicy(Shipped);
        return new AutoSessionCoordinator(_machine, policy, new SessionTrigger(policy), () => true, NullLogger.Instance, clipboard);
    }

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"st-scope-{Guid.NewGuid():N}.db");
    private SqliteSessionStore? _store;
    private SessionMachine? _machine;

    private sealed class FixedKey(byte[] key) : IStoreKeyProvider
    {
        public byte[] GetKey() => (byte[])key.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (_machine is not null)
        {
            await _machine.DisposeAsync();
        }

        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        File.Delete(_path);
    }

    /// <summary>Two windows of one remote tool: same process, same tool id, different handle.</summary>
    private static ForegroundWindowInfo Window(nint handle, string title = "Remote Desktop") =>
        new(handle, 100, "mstsc", title, "Window", null, false, At);
}
