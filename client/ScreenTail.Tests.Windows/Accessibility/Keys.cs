using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ScreenTail.Tests.Windows.Accessibility;

/// <summary>
/// Presses a key in a WPF window the way the message loop does when the keyboard sends one, without
/// sending anything to Windows' input queue (ST-084).
///
/// The window's <see cref="HwndSource"/> is handed the WM_KEYDOWN and WM_KEYUP a real key produces,
/// through <see cref="IKeyboardInputSink.TranslateAccelerator"/>, which is exactly what the dispatcher's
/// loop calls for every keyboard message before it dispatches it. From there it is WPF's own pipeline:
/// the raw report, PreviewKeyDown tunnelling, KeyDown bubbling, input bindings, access keys (Esc on an
/// IsCancel button), keyboard navigation (Tab and the arrows) and the focus visual, which WPF draws only
/// when the keyboard was the last device used.
///
/// Modifiers are read by WPF with GetKeyState, which answers from the calling thread's own keyboard state.
/// <see cref="SetKeyboardState"/> changes that state for this thread only, so Shift, Ctrl and Alt are held
/// for the one press and put back, and no other window or process on the machine sees a key. This is why
/// the suite can drive the keyboard on the hosted runner, where the input-injection tests may not.
/// </summary>
internal static class Keys
{
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_LMENU = 0xA4;

    /// <summary>Presses and releases <paramref name="key"/> in <paramref name="window"/>, with <paramref name="modifiers"/> held. Returns whether WPF handled the press.</summary>
    public static bool Press(Window window, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        ArgumentNullException.ThrowIfNull(window);
        var source = PresentationSource.FromVisual(window) as HwndSource
            ?? throw new InvalidOperationException("The window has no HwndSource: it has not been shown.");
        var saved = new byte[256];
        _ = GetKeyboardState(saved);
        var held = new byte[256];
        Hold(held, modifiers.HasFlag(ModifierKeys.Shift), VK_SHIFT, VK_LSHIFT);
        Hold(held, modifiers.HasFlag(ModifierKeys.Control), VK_CONTROL, VK_LCONTROL);
        Hold(held, modifiers.HasFlag(ModifierKeys.Alt), VK_MENU, VK_LMENU);
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        held[virtualKey] = 0x80;
        if (!SetKeyboardState(held))
        {
            throw new InvalidOperationException($"SetKeyboardState failed ({Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            // Alt makes a key a system key, as Windows reports it: WM_SYSKEYDOWN with the context bit set.
            var system = modifiers.HasFlag(ModifierKeys.Alt);
            var scan = (long)MapVirtualKey((uint)virtualKey, 0);
            var context = system ? 1L << 29 : 0;
            var down = new MSG
            {
                hwnd = source.Handle,
                message = system ? WM_SYSKEYDOWN : WM_KEYDOWN,
                wParam = virtualKey,
                lParam = new IntPtr(1 | (scan << 16) | context),
            };
            var sink = (IKeyboardInputSink)source;
            var handled = sink.TranslateAccelerator(ref down, modifiers);

            held[virtualKey] = 0;
            _ = SetKeyboardState(held);
            var up = new MSG
            {
                hwnd = source.Handle,
                message = system ? WM_SYSKEYUP : WM_KEYUP,
                wParam = virtualKey,
                lParam = new IntPtr(1 | (scan << 16) | context | (1L << 30) | (1L << 31)),
            };
            _ = sink.TranslateAccelerator(ref up, modifiers);
            return handled;
        }
        finally
        {
            _ = SetKeyboardState(saved);
        }
    }

    /// <summary>
    /// Types <paramref name="text"/> at the focused element as WPF's text composition does for WM_CHAR:
    /// PreviewTextInput, then TextInput. How a layout-dependent key like ? reaches the window.
    /// </summary>
    public static void Type(string text)
    {
        var target = Keyboard.FocusedElement ?? throw new InvalidOperationException("Nothing has keyboard focus to type into.");
        _ = TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, target, text));
    }

    private static void Hold(byte[] state, bool down, int generic, int left)
    {
        if (down)
        {
            state[generic] = 0x80;
            state[left] = 0x80;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] state);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
}
