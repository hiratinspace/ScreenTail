using System.Runtime.InteropServices;

namespace ScreenTail.Spike.Capture;

/// <summary>
/// Stand-in technician for unattended runs (<c>--drive-input</c>): types lowercase text and clicks inside
/// Spike.Overlay's typing-target window via SendInput. Injected input passes through the same low-level
/// hooks, so callback cost is measured exactly as for real typing. It never types into any other window.
/// </summary>
internal static class InputDriver
{
    public const string TargetTitle = "ScreenTail.Spike.TypingTarget";

    // Lowercase letters and spaces only, so each character maps straight to a virtual-key code.
    private const string Text = "checked the print spooler service and restarted it then cleared the queue ";
    private const ushort VkReturn = 0x0D;
    private const ushort VkMenu = 0x12;

    public static Task RunAsync(RunReport report, CancellationToken ct) =>
        Task.Factory.StartNew(() => Loop(report, ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Loop(RunReport report, CancellationToken ct)
    {
        var target = WaitForTarget(ct);
        if (target == IntPtr.Zero)
        {
            report.InputDriverNote = "typing target not found; start Spike.Overlay with --typing-target";
            return;
        }

        var random = new Random(7);
        var index = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!EnsureForeground(target))
            {
                report.InputDriverSkips++;
                ct.WaitHandle.WaitOne(500);
                continue;
            }

            var c = Text[index++ % Text.Length];
            PressKey(c == ' ' ? (ushort)0x20 : char.ToUpperInvariant(c));
            report.SyntheticKeys++;

            if (index % Text.Length == 0)
            {
                PressKey(VkReturn);
                report.SyntheticKeys++;
            }

            if (index % 45 == 0)
            {
                ClickCenter(target);
                report.SyntheticClicks++;
            }

            // 80-160 ms between keys: a fast typist, about 8 keys per second.
            ct.WaitHandle.WaitOne(80 + random.Next(80));
        }
    }

    private static IntPtr WaitForTarget(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30 && !ct.IsCancellationRequested; attempt++)
        {
            var hwnd = Native.FindWindow(null, TargetTitle);
            if (hwnd != IntPtr.Zero)
            {
                return hwnd;
            }

            ct.WaitHandle.WaitOne(500);
        }

        return IntPtr.Zero;
    }

    private static bool EnsureForeground(IntPtr target)
    {
        if (Native.GetForegroundWindow() == target)
        {
            return true;
        }

        // A tap of Alt satisfies the foreground-lock rules so SetForegroundWindow is honoured.
        PressKey(VkMenu);
        Native.SetForegroundWindow(target);
        Thread.Sleep(150);
        return Native.GetForegroundWindow() == target;
    }

    private static void PressKey(ushort vk)
    {
        Native.Input[] inputs = [Keyboard(vk, 0), Keyboard(vk, Native.KeyeventfKeyUp)];
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>());
    }

    private static void ClickCenter(IntPtr hwnd)
    {
        var bounds = ScreenGrab.WindowBounds(hwnd);
        if (bounds.IsEmpty)
        {
            return;
        }

        Native.SetCursorPos(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
        Native.Input[] inputs = [Mouse(Native.MouseeventfLeftDown), Mouse(Native.MouseeventfLeftUp)];
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.Input>());
    }

    private static Native.Input Keyboard(ushort vk, uint flags) => new()
    {
        Type = Native.InputKeyboard,
        U = new Native.InputUnion { Ki = new Native.KeybdInput { WVk = vk, DwFlags = flags } },
    };

    private static Native.Input Mouse(uint flags) => new()
    {
        Type = Native.InputMouse,
        U = new Native.InputUnion { Mi = new Native.MouseInput { DwFlags = flags } },
    };
}
