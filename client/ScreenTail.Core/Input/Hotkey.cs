using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ScreenTail.Core.Input;

/// <summary>What a chord does. The names are the ones the tray menu and Settings show (Spec §5 S1).</summary>
public enum HotkeyAction
{
    StartCapture,
    PauseOrResume,
    StopAndDraft,
    MarkMoment,

    /// <summary>
    /// Bindable, but with no default chord — see <see cref="HotkeyBindings.Defaults"/>. Discarding a
    /// session is irreversible and Spec §3 requires a typed confirmation for it, so it is not something a
    /// mistyped chord should begin.
    /// </summary>
    DiscardSession,
}

/// <summary>
/// The values are Windows' own <c>MOD_ALT</c>, <c>MOD_CONTROL</c>, <c>MOD_SHIFT</c> and <c>MOD_WIN</c>, so
/// this can be cast straight into <c>RegisterHotKey</c> with no lookup table to fall out of step. Putting
/// them in alphabetical order, or starting Alt at 2, would silently register the wrong chords.
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// One chord: modifiers plus a key (ST-029).
///
/// Parsed and formatted here rather than in the UI so that the chord a tenant's policy file contains, the
/// chord Settings shows, and the chord Windows is asked to register are the same string read the same way.
/// </summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>
    /// Windows will register a bare key, and then the technician cannot type that letter anywhere else on
    /// the machine for the rest of the session. A global chord needs a modifier that is not Shift alone.
    /// </summary>
    public bool IsUsable =>
        !string.IsNullOrEmpty(Key)
        && (Modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Windows)) != 0;

    public static bool TryParse(string? text, [NotNullWhen(true)] out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN" or "WINDOWS":
                    modifiers |= HotkeyModifiers.Windows;
                    break;
                default:
                    if (key is not null)
                    {
                        // "Ctrl+A+B" is not a chord anyone meant; refusing beats registering one of them.
                        return false;
                    }

                    key = raw.ToUpperInvariant();
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }

    /// <summary>The virtual-key code Windows registers, for single letters, digits and F-keys.</summary>
    public int? VirtualKey => Key switch
    {
        [var c] when c is >= 'A' and <= 'Z' => c,
        [var c] when c is >= '0' and <= '9' => c,
        ['F', .. var rest] when int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24 => 0x6F + n,
        _ => null,
    };
}
