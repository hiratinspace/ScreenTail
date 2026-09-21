using System.Diagnostics;
using ScreenTail.Core.Input;
using ScreenTail.Core.Sessions;
using ScreenTail.Shared.Ipc;
using ScreenTail.Shared.Schema;
using ScreenTail.Tests.Sessions;

namespace ScreenTail.Tests.Input;

/// <summary>ST-029: the chords, what they do, and what Settings says when one cannot be had.</summary>
public sealed class HotkeyTests : IAsyncDisposable
{
    private static readonly RemoteTool Rdp = new() { Kind = RemoteToolKind.Rdp };
    private MachineHarness? _harness;

    [Theory]
    [InlineData("Ctrl+Alt+P", HotkeyModifiers.Control | HotkeyModifiers.Alt, "P")]
    [InlineData("ctrl+alt+p", HotkeyModifiers.Control | HotkeyModifiers.Alt, "P")]
    [InlineData("Control + Alt + M", HotkeyModifiers.Control | HotkeyModifiers.Alt, "M")]
    [InlineData("Ctrl+Shift+F9", HotkeyModifiers.Control | HotkeyModifiers.Shift, "F9")]
    [InlineData("Win+Alt+R", HotkeyModifiers.Windows | HotkeyModifiers.Alt, "R")]
    public void AChordReadsTheSameHoweverItWasWritten(string text, HotkeyModifiers modifiers, string key)
    {
        // The same string has to mean the same chord in a policy file, in Settings and in the call to
        // Windows. Spacing and case are how a hand-edited policy file differs from a generated one.
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    [InlineData(null)]
    public void AChordThatIsNotOneIsRefused(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void AChordSurvivesBeingWrittenOutAndReadBack()
    {
        foreach (var (_, hotkey) in HotkeyBindings.Defaults)
        {
            Assert.True(Hotkey.TryParse(hotkey.ToString(), out var round));
            Assert.Equal(hotkey, round!.Value);
        }
    }

    [Fact]
    public void TheDefaultsAreTheOnesTheTrayMenuShows()
    {
        // Spec §5 S1 prints these next to the menu items. If they drift apart, the menu tells the
        // technician to press something that does nothing.
        var bindings = new HotkeyBindings();

        Assert.Equal("Ctrl+Alt+R", bindings.For(HotkeyAction.StartCapture)?.ToString());
        Assert.Equal("Ctrl+Alt+P", bindings.For(HotkeyAction.PauseOrResume)?.ToString());
        Assert.Equal("Ctrl+Alt+S", bindings.For(HotkeyAction.StopAndDraft)?.ToString());
        Assert.Equal("Ctrl+Alt+M", bindings.For(HotkeyAction.MarkMoment)?.ToString());
        Assert.Empty(bindings.Validate());
    }

    [Fact]
    public void DiscardHasNoChordByDefault()
    {
        // Deliberate. It is irreversible, Spec §3 requires a typed confirmation, and a chord sitting next
        // to the other four is one somebody hits by accident on the wrong day.
        Assert.Null(new HotkeyBindings().For(HotkeyAction.DiscardSession));
    }

    [Fact]
    public void TwoActionsOnOneChordIsReportedWithAWayOut()
    {
        // AC2. The warning has to name the action that lost, say what took it, and offer something free —
        // a warning that only says "conflict" leaves the technician to guess a chord and try again.
        var bindings = new HotkeyBindings(new Dictionary<HotkeyAction, Hotkey>
        {
            [HotkeyAction.PauseOrResume] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "P"),
            [HotkeyAction.MarkMoment] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "P"),
        });

        var conflict = Assert.Single(bindings.Validate());

        Assert.Equal(HotkeyAction.MarkMoment, conflict.Action);
        Assert.Contains("Pause", conflict.Reason, StringComparison.Ordinal);
        Assert.NotNull(conflict.Suggestion);
        Assert.True(conflict.Suggestion!.Value.IsUsable);
        Assert.DoesNotContain(conflict.Suggestion.Value, bindings.All.Values);
    }

    [Fact]
    public void AChordWindowsAlreadyGaveAwayIsReportedTheSameWay()
    {
        // RegisterHotKey just fails, with no way to ask who holds the chord. Settings still has to say
        // which action ended up with no shortcut, and offer one.
        var bindings = new HotkeyBindings();
        var taken = bindings.For(HotkeyAction.MarkMoment)!.Value;

        var conflict = bindings.Refused(HotkeyAction.MarkMoment, taken);

        Assert.Contains("Another application", conflict.Reason, StringComparison.Ordinal);
        Assert.Contains("Mark moment", conflict.Reason, StringComparison.Ordinal);
        Assert.NotNull(conflict.Suggestion);
    }

    [Fact]
    public void AChordWithNoModifierIsRefusedBeforeItIsRegistered()
    {
        // Windows would accept it, and then "M" would do nothing but mark moments anywhere on the machine
        // for the rest of the day — including in the middle of a customer's password.
        var bindings = new HotkeyBindings(new Dictionary<HotkeyAction, Hotkey>
        {
            [HotkeyAction.MarkMoment] = new(HotkeyModifiers.None, "M"),
            [HotkeyAction.PauseOrResume] = new(HotkeyModifiers.Shift, "P"),
        });

        var conflicts = bindings.Validate();

        Assert.Equal(2, conflicts.Count);
        Assert.All(conflicts, c => Assert.Contains("Ctrl, Alt or Win", c.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void AKeyWindowsHasNoNameForIsRefused()
    {
        var bindings = new HotkeyBindings(new Dictionary<HotkeyAction, Hotkey>
        {
            [HotkeyAction.MarkMoment] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "SPLAT"),
        });

        Assert.Contains("no key called", Assert.Single(bindings.Validate()).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASuggestionIsNeverOneAlreadyInUse()
    {
        // Offering a chord that collides with another of ours would send the technician round the loop
        // again. Bind every first-choice alternative and the suggestion has to move on.
        var bindings = new HotkeyBindings();
        for (var i = 0; i < 4; i++)
        {
            _ = bindings.Bind((HotkeyAction)i, new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, $"F{9 + i}"));
        }

        var suggestion = bindings.Suggest();

        Assert.NotNull(suggestion);
        Assert.DoesNotContain(suggestion!.Value, bindings.All.Values);
    }

    [Fact]
    public async Task ADiscardChordDoesNotThrowTheSessionAway()
    {
        // The binding has no default chord because discarding is irreversible and Spec §3 wants a typed
        // confirmation -- but the router performed it anyway, so the rule lived in the absence of a
        // default rather than in the code. The moment bindings become editable (ST-047) that is one
        // mistyped chord and a session gone, with no confirmation anywhere in the path (2026-09-20
        // review).
        //
        // A chord cannot carry a confirmation, so this route cannot be the one that discards.
        var machine = await RecordingAsync();
        var router = new HotkeyRouter(machine);

        var acted = await router.InvokeAsync(HotkeyAction.DiscardSession, TestContext.Current.CancellationToken);

        Assert.False(acted);
        Assert.Equal(SessionState.Recording, machine.State);
    }

    [Fact]
    public async Task ThePauseChordPausesWithinTwoHundredMilliseconds()
    {
        // AC1. The HUD pill itself says "press Ctrl+Alt+P to resume", so the technician is looking straight
        // at the thing that has to change when they press it.
        var machine = await RecordingAsync();
        var router = new HotkeyRouter(machine);
        var announced = new TaskCompletionSource<CaptureStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        machine.StateChanged += s =>
        {
            if (s.State == CaptureStates.Paused)
            {
                announced.TrySetResult(s);
            }
        };

        var clock = Stopwatch.StartNew();
        Assert.True(await router.InvokeAsync(HotkeyAction.PauseOrResume, TestContext.Current.CancellationToken));
        var snapshot = await announced.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Stop();

        Assert.Equal(SessionState.Paused, machine.State);
        Assert.Equal(CaptureStates.Paused, snapshot.State);
        Assert.True(clock.ElapsedMilliseconds < 200, $"pausing and announcing it took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task TheSameChordResumes()
    {
        // One chord, both ways. A technician who paused to type a password presses the same keys to carry
        // on, and a second chord is one more thing to remember at exactly the wrong moment.
        var machine = await RecordingAsync();
        var router = new HotkeyRouter(machine);

        Assert.True(await router.InvokeAsync(HotkeyAction.PauseOrResume, TestContext.Current.CancellationToken));
        Assert.Equal(SessionState.Paused, machine.State);

        Assert.True(await router.InvokeAsync(HotkeyAction.PauseOrResume, TestContext.Current.CancellationToken));
        Assert.Equal(SessionState.Recording, machine.State);
    }

    [Fact]
    public async Task TheChordDoesNotUndoAPrivacySuppression()
    {
        // Suppressed belongs to the password guard (ST-040) and the login heuristic (ST-041). A chord that
        // resumed out of it would be a way to switch a privacy control off by accident, at the one moment
        // it is doing its job.
        var machine = await RecordingAsync();
        Assert.True(await machine.SuppressAsync(CaptureStateReason.PasswordField, TestContext.Current.CancellationToken));
        var router = new HotkeyRouter(machine);

        Assert.False(await router.InvokeAsync(HotkeyAction.PauseOrResume, TestContext.Current.CancellationToken));
        Assert.Equal(SessionState.Suppressed, machine.State);
    }

    [Fact]
    public async Task MarkMomentRecordsAMarkerAndAsksForAFrame()
    {
        // AC3. The marker path never went through the click debouncer, so "regardless of debounce" is a
        // property of the design — this is the test that keeps it one.
        var machine = await RecordingAsync();
        var router = new HotkeyRouter(machine);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(await router.InvokeAsync(HotkeyAction.MarkMoment, TestContext.Current.CancellationToken));
        }

        var session = (await _harness!.Store.LoadSessionAsync(machine.SessionId!))!;
        Assert.Equal(5, session.Events.OfType<MarkerEvent>().Count());
    }

    [Fact]
    public async Task NothingHappensWhenThereIsNoSessionToActOn()
    {
        // Chords are global: they fire whether or not a session is running, including while the technician
        // is doing something else entirely. Each one has to be a no-op rather than an error.
        _harness = await MachineHarness.StartAsync();
        var router = new HotkeyRouter(_harness.Machine);

        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            Assert.False(await router.InvokeAsync(action, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task StartCaptureDoesNothingUntilSomethingCanSayWhatToStart()
    {
        // Starting needs a remote tool to attribute the session to, and only the foreground coordinator
        // knows that. Without it the chord reports it did nothing rather than inventing a tool.
        _harness = await MachineHarness.StartAsync();
        var router = new HotkeyRouter(_harness.Machine);

        Assert.False(await router.InvokeAsync(HotkeyAction.StartCapture, TestContext.Current.CancellationToken));

        var withStart = new HotkeyRouter(_harness.Machine, ct => _harness.Machine.StartAsync(Rdp, ct: ct));
        Assert.True(await withStart.InvokeAsync(HotkeyAction.StartCapture, TestContext.Current.CancellationToken));
        Assert.Equal(SessionState.Recording, _harness.Machine.State);
    }

    private async Task<SessionMachine> RecordingAsync()
    {
        _harness = await MachineHarness.StartAsync();
        Assert.True(await _harness.Machine.StartAsync(Rdp, localOnly: false, policyVersion: null));
        return _harness.Machine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}
