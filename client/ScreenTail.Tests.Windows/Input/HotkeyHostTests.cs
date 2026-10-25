using System.Collections.Concurrent;
using ScreenTail.Core.Input;
using ScreenTail.Service.Input;

namespace ScreenTail.Tests.Windows.Input;

/// <summary>
/// ST-080 against Windows itself: the chords the service holds across a change of bindings.
/// <see cref="HotkeyRegistrationTests"/> covers one fixed set; this covers the swap, which is where a
/// chord can be left registered by a set that is gone, or the handler left wired to one that is.
/// </summary>
public sealed class HotkeyHostTests
{
    private static bool OnWindows => OperatingSystem.IsWindows();

    [Fact]
    public async Task RebindingReleasesTheOldChordAndKeepsTheHandler()
    {
        // 2026-10-12 review (T-31). RebindAsync had no test. Two things can go wrong in it and neither
        // shows up in Conflicts: the old set is not released, so the technician's old chord goes on
        // doing something and nobody else can have it; or the new set is made without the handler, so
        // the new chord registers and does nothing. The chords are not the defaults, so a ScreenTail
        // installed on this machine does not take them first, and they are three-modifier letters rather
        // than Ctrl+Alt function keys, which graphics drivers hold (the laptop's holds Ctrl+Alt+F8).
        Assert.SkipUnless(OnWindows, "Windows only.");
        var ct = TestContext.Current.CancellationToken;
        var before = Chords("H", "L", "N", "O");
        var after = Chords("G", "Q", "V", "Z");
        var pressed = new ConcurrentQueue<HotkeyAction>();
        var arrived = new SemaphoreSlim(0);

        await using var host = new HotkeyHost(new HotkeyBindings(before), action =>
        {
            pressed.Enqueue(action);
            arrived.Release();
        });
        await host.StartAsync(ct);
        Assert.Empty(host.Conflicts);

        var conflicts = await host.RebindAsync(new HotkeyBindings(after), ct);
        Assert.Empty(conflicts);

        // The old set is free again: another registrant gets every one of them.
        await using (var again = new Hotkeys(new HotkeyBindings(before)))
        {
            await again.StartAsync(ct);
            Assert.Empty(again.Conflicts);
        }

        // The new set is held: another registrant gets none of them.
        await using (var clash = new Hotkeys(new HotkeyBindings(after)))
        {
            await clash.StartAsync(ct);
            Assert.Equal(after.Count, clash.Conflicts.Count);
        }

        // The handler came along: a press of a new chord reaches it, as the action it was bound to. The
        // old chord is nobody's now, so striking it reaches nothing.
        Assert.False(host.Press(before[HotkeyAction.PauseOrResume]));
        Assert.True(host.Press(after[HotkeyAction.PauseOrResume]));
        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5), ct), "The rebound chord was pressed and the handler never heard of it.");
        Assert.True(pressed.TryDequeue(out var action));
        Assert.Equal(HotkeyAction.PauseOrResume, action);
    }

    private const HotkeyModifiers Three = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;

    private static Dictionary<HotkeyAction, Hotkey> Chords(string start, string pause, string stop, string mark) => new()
    {
        [HotkeyAction.StartCapture] = new(Three, start),
        [HotkeyAction.PauseOrResume] = new(Three, pause),
        [HotkeyAction.StopAndDraft] = new(Three, stop),
        [HotkeyAction.MarkMoment] = new(Three, mark),
    };
}
