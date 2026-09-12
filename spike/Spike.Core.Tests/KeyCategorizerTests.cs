namespace ScreenTail.Spike.Core.Tests;

public class KeyCategorizerTests
{
    [Theory]
    [InlineData(0x41, false, false, false, KeyCategory.Character)] // A
    [InlineData(0x31, false, false, false, KeyCategory.Character)] // 1
    [InlineData(0x20, false, false, false, KeyCategory.Character)] // Space
    [InlineData(0x08, false, false, false, KeyCategory.Character)] // Backspace
    [InlineData(0xBE, false, false, false, KeyCategory.Character)] // period
    [InlineData(0x0D, false, false, false, KeyCategory.Enter)]
    [InlineData(0x41, true, false, false, KeyCategory.Shortcut)] // Ctrl+A
    [InlineData(0x0D, true, false, false, KeyCategory.Shortcut)] // Ctrl+Enter
    [InlineData(0x44, false, false, true, KeyCategory.Shortcut)] // Win+D
    [InlineData(0x70, true, true, false, KeyCategory.Shortcut)] // Ctrl+Alt+F1
    [InlineData(0x51, true, true, false, KeyCategory.Character)] // AltGr+Q (@ on German layout)
    [InlineData(0x10, false, false, false, KeyCategory.Modifier)] // Shift
    [InlineData(0xA2, true, false, false, KeyCategory.Modifier)] // LCtrl while Ctrl down
    [InlineData(0x70, false, false, false, KeyCategory.Other)] // F1
    [InlineData(0x25, false, false, false, KeyCategory.Other)] // Left arrow
    [InlineData(0x09, false, false, false, KeyCategory.Other)] // Tab
    public void Categorize(int vk, bool ctrl, bool alt, bool win, KeyCategory expected)
    {
        Assert.Equal(expected, KeyCategorizer.Categorize(vk, ctrl, alt, win));
    }
}
