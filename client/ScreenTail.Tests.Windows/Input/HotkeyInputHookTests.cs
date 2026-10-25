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
/// Windows machine this presses each chord by posting the message Windows would post, which needs no
/// keyboard and so never skips. On the laptop, where <c>SCREENTAIL_ALLOW_INPUT_INJECTION=1</c>, it also
/// holds a real chord down through the keyboard's auto-repeat, which is the one thing MOD_NOREPEAT is
/// for and the one thing only a keyboard can check.
///
/// Named for the laptop's input job, which runs the classes called <c>*InputHookTests</c> and nothing
/// else; the hosted and capabilities jobs run the whole assembly and get the message half.
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

        // Not the defaults, so a ScreenTail installed on this machine does not hold them first.
        var chords = new Dictionary<HotkeyAction, Hotkey>
        {
            [HotkeyAction.StartCapture] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "F9"),
            [HotkeyAction.PauseOrResume] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "F10"),
            [HotkeyAction.StopAndDraft] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "F11"),
            [HotkeyAction.MarkMoment] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "F12"),
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
        Assert.Empty(hotkeys.Conflicts);

        // Every chord is registered with MOD_NOREPEAT, whatever its modifiers.
        Assert.All(chords.Values, chord => Assert.Equal(MOD_NOREPEAT, Hotkeys.RegistrationFlags(chord) & MOD_NOREPEAT));

        // Each message becomes its own action, once. Pressed in an order that is not the binding order,
        // so a mapping that was off by one would be caught rather than cancel out.
        foreach (var expected in new[] { HotkeyAction.MarkMoment, HotkeyAction.StartCapture, HotkeyAction.StopAndDraft, HotkeyAction.PauseOrResume })
        {
            Assert.True(hotkeys.Press(expected), $"{expected} is not registered, so it cannot be pressed.");
            Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5), ct), $"{expected} was pressed and nothing was raised.");
            Assert.True(pressed.TryDequeue(out var raised));
            Assert.Equal(expected, raised);
        }

        await Task.Delay(300, ct);
        Assert.True(pressed.IsEmpty, $"Four presses raised more than four actions: {string.Join(", ", pressed)}.");

        // A chord nobody registered is not a press.
        Assert.False(hotkeys.Press(HotkeyAction.DiscardSession));

        if (!CanInject)
        {
            Measurements.Record("Hotkey mapping checked by message; the keyboard's auto-repeat needs the laptop.");
            return;
        }

        // The keyboard. Held down, Windows repeats the key; MOD_NOREPEAT is what makes that one press.
        using var window = DesktopWindow.Create("ScreenTail hotkey target");
        window.RequireForeground();
        Synthetic.HoldChord(VK_CONTROL, VK_MENU, VK_F12, repeats: 3);

        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5), ct), "A real Ctrl+Alt+F12 raised nothing.");
        Assert.True(pressed.TryDequeue(out var fromKeyboard));
        Assert.Equal(HotkeyAction.MarkMoment, fromKeyboard);
        await Task.Delay(500, ct);
        Assert.True(pressed.IsEmpty, "Holding the chord down raised the action more than once; MOD_NOREPEAT is not in effect.");
        Measurements.Record("A chord held through three auto-repeats raised its action once");
    }

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_F12 = 0x7B;

    /// <summary>Synthetic keyboard input, as the hook tests send it, for a chord held down.</summary>
    private static class Synthetic
    {
        /// <summary>Presses two modifiers, strikes the key and then repeats it as a held key does, and lets go in reverse.</summary>
        public static void HoldChord(ushort first, ushort second, ushort key, int repeats)
        {
            Send(Keyboard(first, up: false));
            Send(Keyboard(second, up: false));
            for (var i = 0; i <= repeats; i++)
            {
                Send(Keyboard(key, up: false));
                Thread.Sleep(30);
            }

            Send(Keyboard(key, up: true));
            Send(Keyboard(second, up: true));
            Send(Keyboard(first, up: true));
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
