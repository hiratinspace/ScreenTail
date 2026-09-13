using System.Runtime.InteropServices;

namespace ScreenTail.Core.Input;

/// <summary>What a hook callback is allowed to say happened. Deliberately coarse (INV-2).</summary>
public enum InputKind : byte
{
    Click,

    /// <summary>A key that produces a character. Which character is never determined, let alone stored.</summary>
    PrintableKey,

    Enter,

    /// <summary>A non-printing key pressed with a modifier. Which key is discarded inside the callback.</summary>
    Shortcut,
}

public enum MouseButtonKind : byte
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// One observation from the input hooks (ST-024). A struct, so the ring buffer is a flat array with nothing
/// to allocate on the input path.
///
/// There is no field here that can hold a key. Not "we choose not to write one" — there is nowhere to put
/// it. That is how INV-2 is kept: the callback classifies into <see cref="InputKind"/> and the virtual-key
/// code goes out of scope before the callback returns, so no later mistake can start recording keystrokes
/// without changing this type and every test that pins it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct InputSignal(InputKind kind, long timestamp, int x = 0, int y = 0, MouseButtonKind button = MouseButtonKind.Left)
{
    public InputKind Kind { get; } = kind;

    public MouseButtonKind Button { get; } = button;

    /// <summary>Monotonic timestamp from the hook thread, converted to session time by the consumer.</summary>
    public long Timestamp { get; } = timestamp;

    /// <summary>Physical screen pixels; negative on monitors left of or above the primary one.</summary>
    public int X { get; } = x;

    public int Y { get; } = y;
}
