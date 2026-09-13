using ScreenTail.Core.Input;
using ScreenTail.Service.Input;

namespace ScreenTail.Tests.Windows.Input;

/// <summary>
/// ST-029 against Windows itself. <see cref="ScreenTail.Tests"/> covers which chord does what and what
/// Settings says about a conflict; this covers whether the chords can be had at all on a real machine,
/// which is the part no fake can answer.
/// </summary>
public sealed class HotkeyRegistrationTests
{
    private static bool OnWindows => OperatingSystem.IsWindows();

    [Fact]
    public async Task TheDefaultChordsAreAvailableOnThisMachine()
    {
        // If any of Ctrl+Alt+R/P/S/M is already taken on a normal Windows install, the defaults in Spec
        // §5 S1 are the wrong defaults and the tray menu would print a shortcut that does nothing.
        Assert.SkipUnless(OnWindows, "Windows only.");
        await using var hotkeys = new Hotkeys();

        await hotkeys.StartAsync(TestContext.Current.CancellationToken);

        var conflicts = hotkeys.Conflicts;
        Measurements.Record(conflicts.Count == 0
            ? $"All {HotkeyBindings.Defaults.Count} default chords registered with Windows"
            : "Chords Windows would not give us: "
                + string.Join(", ", conflicts.Select(c => $"{c.Hotkey} ({c.Reason})")));

        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task AChordAnotherProcessHoldsIsReportedRatherThanThrown()
    {
        // The case that decides whether a technician loses a shortcut or a capture service: two instances
        // wanting the same chord. The second must come up, say which action has no shortcut, and carry on.
        Assert.SkipUnless(OnWindows, "Windows only.");
        await using var first = new Hotkeys();
        await first.StartAsync(TestContext.Current.CancellationToken);
        Assert.Empty(first.Conflicts);

        await using var second = new Hotkeys();
        await second.StartAsync(TestContext.Current.CancellationToken);

        var conflicts = second.Conflicts;
        Assert.NotEmpty(conflicts);
        Assert.All(conflicts, c => Assert.Contains("Another application", c.Reason, StringComparison.Ordinal));
        Assert.All(conflicts, c => Assert.NotNull(c.Suggestion));
        Measurements.Record(
            $"A second instance lost **{conflicts.Count}** chords to the first and started anyway, "
            + $"suggesting {conflicts[0].Suggestion}");
    }

    [Fact]
    public async Task ChordsAreHandedBackWhenTheServiceStops()
    {
        // A shortcut left registered by a process that has gone is a shortcut nobody can have until the
        // machine is restarted. Starting, stopping and starting again has to work.
        Assert.SkipUnless(OnWindows, "Windows only.");
        await using (var first = new Hotkeys())
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            Assert.Empty(first.Conflicts);
        }

        await using var second = new Hotkeys();
        await second.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(second.Conflicts);
    }
}
