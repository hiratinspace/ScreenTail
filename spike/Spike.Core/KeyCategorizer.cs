namespace ScreenTail.Spike.Core;

public enum KeyCategory
{
    Character,
    Enter,
    Shortcut,
    Modifier,
    Other,
}

/// <summary>
/// Maps a virtual-key code to a category at the hook boundary so the key identity is never stored (INV-2).
/// </summary>
public static class KeyCategorizer
{
    private const int VkBack = 0x08;
    private const int VkReturn = 0x0D;
    private const int VkSpace = 0x20;
    private const int VkDelete = 0x2E;

    public static KeyCategory Categorize(int virtualKey, bool ctrlDown, bool altDown, bool winDown)
    {
        if (IsModifier(virtualKey))
        {
            return KeyCategory.Modifier;
        }

        // AltGr arrives as Ctrl+Alt and produces characters on many layouts; treat it as typing.
        var altGr = ctrlDown && altDown && !winDown;
        if (altGr && IsCharacterProducing(virtualKey))
        {
            return KeyCategory.Character;
        }

        if (ctrlDown || altDown || winDown)
        {
            return KeyCategory.Shortcut;
        }

        if (virtualKey == VkReturn)
        {
            return KeyCategory.Enter;
        }

        return IsCharacterProducing(virtualKey) ? KeyCategory.Character : KeyCategory.Other;
    }

    public static bool IsModifier(int vk) =>
        vk is >= 0x10 and <= 0x12      // Shift, Ctrl, Alt
            or >= 0xA0 and <= 0xA5     // L/R Shift, Ctrl, Alt
            or 0x5B or 0x5C            // L/R Win
            or 0x14;                   // Caps Lock

    private static bool IsCharacterProducing(int vk) =>
        vk is VkBack or VkSpace or VkDelete
            or >= 0x30 and <= 0x39     // 0-9
            or >= 0x41 and <= 0x5A     // A-Z
            or >= 0x60 and <= 0x6F     // numpad digits and operators
            or >= 0xBA and <= 0xC0     // OEM 1, plus, comma, minus, period, 2, 3
            or >= 0xDB and <= 0xDF     // OEM 4-8
            or 0xE2;                   // OEM 102
}
