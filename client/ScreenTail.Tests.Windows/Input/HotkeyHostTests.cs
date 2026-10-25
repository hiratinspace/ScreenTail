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
        // installed on this machine does not take them first.
        Assert.SkipUnless(OnWindows, "Windows only.");
        var ct = TestContext.Current.CancellationToken;
        var before = Chords(HotkeyModifiers.Control | HotkeyModifiers.Shift);
        var after = Chords(HotkeyModifiers.Control | HotkeyModifiers.Alt);
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

        // The handler came along: a press of a new chord reaches it, as the action it was bound to.
        Assert.True(host.Press(HotkeyAction.PauseOrResume));
        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5), ct), "The rebound chord was pressed and the handler never heard of it.");
        Assert.True(pressed.TryDequeue(out var action));
        Assert.Equal(HotkeyAction.PauseOrResume, action);
    }

    private static Dictionary<HotkeyAction, Hotkey> Chords(HotkeyModifiers modifiers) => new()
    {
        [HotkeyAction.StartCapture] = new(modifiers, "F5"),
        [HotkeyAction.PauseOrResume] = new(modifiers, "F6"),
        [HotkeyAction.StopAndDraft] = new(modifiers, "F7"),
        [HotkeyAction.MarkMoment] = new(modifiers, "F8"),
    };
}
