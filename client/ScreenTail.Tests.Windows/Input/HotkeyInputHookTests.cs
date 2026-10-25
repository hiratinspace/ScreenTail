using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ScreenTail.Core.Capabilities;
using ScreenTail.Core.Input;
using ScreenTail.Service.Capabilities;
using ScreenTail.Service.Input;

namespace ScreenTail.Tests.Windows.Input;

/// <summary>
/// ST-029 against the message pump: what a WM_HOTKEY becomes (2026-10-12 review, T-32).
///
/// The mapping from a registration id to an action lived in a local the pump kept, and nothing checked
/// it: Pause could have ended up mapped to StopAndDraft and every test would have stayed green. On any
/// Windows machine this strikes each chord by posting the message Windows would post for it, found by
/// the chord's modifiers and key in what <c>RegisterHotKey</c> was actually given, so the press does not
/// go through the mapping it checks. It also reads the flags each registration was made with, which is
/// where MOD_NOREPEAT has to be. On the laptop, where <c>SCREENTAIL_ALLOW_INPUT_INJECTION=1</c>, it then
/// strikes every chord on the keyboard and holds one down through the keyboard's auto-repeat, the one
/// thing MOD_NOREPEAT is for and the one thing only a keyboard can check.
///
/// Named for the laptop's input job, which runs the classes called <c>*InputHookTests</c> and nothing
/// else; the hosted and capabilities jobs run the whole assembly and get the message half. Without
/// injection the test passes on the message half and says so in the measurements rather than skipping:
/// the hosted budget (10) and the capabilities budget (4) are each already spent on the four injection
/// tests and the performance tests, and a fifth skip would fail both. Where injection is meant to work
/// and does not, the input job's other injection tests skip and fail its zero budget.
/// </summary>
public sealed class HotkeyInputHookTests
{
    private const uint MOD_NOREPEAT = 0x4000;

    private static bool CanInject =>
        Environment.GetEnvironmentVariable("SCREENTAIL_ALLOW_INPUT_INJECTION") == "1"
        && new WindowsCapabilityProbe().Probe()[Capability.DesktopSession].State == CapabilityState.Ok;

    [Fact]
    public async Task PressingAChordRaisesItsOwnActionOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var ct = TestContext.Current.CancellationToken;

        // Not the defaults, so a ScreenTail installed on this machine does not hold them first. Not
        // Ctrl+Alt with an F-key either: graphics drivers register some of those for themselves (Intel's
        // takes Ctrl+Alt+F12), and Windows would refuse the chord here for a reason that has nothing to
        // do with this code. Three modifiers and a letter is a chord applications rarely claim.
        const HotkeyModifiers three = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
        var chords = new Dictionary<HotkeyAction, Hotkey>
        {
            [HotkeyAction.StartCapture] = new(three, "J"),
            [HotkeyAction.PauseOrResume] = new(three, "K"),
            [HotkeyAction.StopAndDraft] = new(three, "U"),
            [HotkeyAction.MarkMoment] = new(three, "Y"),
        };
        var bindings = new HotkeyBindings(chords);
        Assert.Empty(bindings.Validate());

        var pressed = new ConcurrentQueue<HotkeyAction>();
        var arrived = new SemaphoreSlim(0);
        await using var hotkeys = new Hotkeys(bindings);
        hotkeys.Pressed += action =>
        {
            pressed.Enqueue(action);
            arrived.Release();
        };
        await hotkeys.StartAsync(ct);

        // A chord something else on this machine already holds is the machine's problem, not the
        // mapping's, and it should read as one.
        if (hotkeys.Conflicts.Count > 0)
        {
            Assert.Fail(
                "Windows refused chords this test needs, so another application on this machine holds them: "
                + string.Join(", ", hotkeys.Conflicts.Select(c => c.Hotkey.ToString()))
                + ". Pick chords nothing on this machine registers; the mapping itself was not checked.");
        }

        // What RegisterHotKey was given: every chord once, each with its own modifiers and MOD_NOREPEAT.
        var registered = hotkeys.Registrations;
        Assert.Equal(chords.Count, registered.Count);
        foreach (var (action, chord) in chords)
        {
            var made = Assert.Single(registered, r => r.VirtualKey == (uint)chord.VirtualKey!.Value);
            Assert.Equal((uint)chord.Modifiers, made.Flags & ~MOD_NOREPEAT);
            Assert.True((made.Flags & MOD_NOREPEAT) != 0, $"{chord} was registered without MOD_NOREPEAT, so holding it down repeats {action}.");
        }

        // Each chord, struck by message, becomes its own action, once. In an order that is not the
        // binding order, so a mapping that was off by one, or two actions swapped, cannot cancel out.
        foreach (var expected in new[] { HotkeyAction.MarkMoment, HotkeyAction.StartCapture, HotkeyAction.StopAndDraft, HotkeyAction.PauseOrResume })
        {
            var chord = bindings.For(expected)!.Value;
            Assert.True(hotkeys.Press(chord), $"{chord} is not registered, so it cannot be pressed.");
            await ExpectAsync(expected, chord, "by message");
        }

        await Task.Delay(300, ct);
        Assert.True(pressed.IsEmpty, $"Four presses raised more than four actions: {string.Join(", ", pressed)}.");

        // A chord nobody registered is not a press.
        Assert.False(hotkeys.Press(new Hotkey(three, "Q")));

        if (!CanInject)
        {
            Measurements.Record("Hotkey mapping and MOD_NOREPEAT checked by message and registration; real chords need the laptop's input job.");
            return;
        }

        // The keyboard: every chord struck for real, in yet another order, becomes its own action.
        using var window = DesktopWindow.Create("ScreenTail hotkey target");
        window.RequireForeground();
        foreach (var expected in new[] { HotkeyAction.PauseOrResume, HotkeyAction.MarkMoment, HotkeyAction.StopAndDraft, HotkeyAction.StartCapture })
        {
            var chord = bindings.For(expected)!.Value;
            Synthetic.Chord(chord, repeats: 0);
            await ExpectAsync(expected, chord, "on the keyboard");
        }

        await Task.Delay(300, ct);
        Assert.True(pressed.IsEmpty, $"Four real chords raised more than four actions: {string.Join(", ", pressed)}.");

        // Held down, Windows repeats the key; MOD_NOREPEAT is what makes that one press.
        var held = bindings.For(HotkeyAction.MarkMoment)!.Value;
        Synthetic.Chord(held, repeats: 3);
        await ExpectAsync(HotkeyAction.MarkMoment, held, "held down");
        await Task.Delay(500, ct);
        Assert.True(pressed.IsEmpty, "Holding the chord down raised the action more than once; MOD_NOREPEAT is not in effect.");
        Measurements.Record("Four real chords each raised their own action, and one held through three auto-repeats raised it once");

        async Task ExpectAsync(HotkeyAction expected, Hotkey chord, string how)
        {
            Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5), ct), $"{chord} was struck {how} and nothing was raised.");
            Assert.True(pressed.TryDequeue(out var raised));
            Assert.True(expected == raised, $"{chord} was struck {how} and raised {raised}, not {expected}.");
        }
    }

    /// <summary>Synthetic keyboard input, as the hook tests send it, for a chord struck or held down.</summary>
    private static class Synthetic
    {
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_LWIN = 0x5B;

        /// <summary>
        /// Presses the chord's modifiers, strikes its key and then repeats it as a held key does
        /// (<paramref name="repeats"/> more key-downs), and lets go in reverse.
        /// </summary>
        public static void Chord(Hotkey chord, int repeats)
        {
            var modifiers = new List<ushort>(4);
            if (chord.Modifiers.HasFlag(HotkeyModifiers.Control))
            {
                modifiers.Add(VK_CONTROL);
            }

            if (chord.Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                modifiers.Add(VK_MENU);
            }

            if (chord.Modifiers.HasFlag(HotkeyModifiers.Shift))
            {
                modifiers.Add(VK_SHIFT);
            }

            if (chord.Modifiers.HasFlag(HotkeyModifiers.Windows))
            {
                modifiers.Add(VK_LWIN);
            }

            var key = (ushort)chord.VirtualKey!.Value;
            foreach (var modifier in modifiers)
            {
                Send(Keyboard(modifier, up: false));
            }

            for (var i = 0; i <= repeats; i++)
            {
                Send(Keyboard(key, up: false));
                Thread.Sleep(30);
            }

            Send(Keyboard(key, up: true));
            for (var i = modifiers.Count - 1; i >= 0; i--)
            {
                Send(Keyboard(modifiers[i], up: true));
            }
        }

        private static Input Keyboard(ushort virtualKey, bool up) => new()
        {
            Type = INPUT_KEYBOARD,
            Data = { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = up ? KEYEVENTF_KEYUP : 0 } },
        };

        private static void Send(Input input)
        {
            var one = new[] { input };
            _ = SendInput(1, one, Marshal.SizeOf<Input>());
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MouseInput Mouse;
            [FieldOffset(0)]
            public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint Data;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);
    }
}
