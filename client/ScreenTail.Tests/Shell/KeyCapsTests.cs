using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// How the shortcut sheet draws a chord's keys (Spec §4, v0.6.11): one key cap per chord, alternatives
/// side by side with a comma, and nothing that is not a key inside a cap.
/// </summary>
public sealed class KeyCapsTests
{
    [Theory]
    [InlineData("?", new[] { "?" })]
    [InlineData("Ctrl+Alt+R", new[] { "Ctrl+Alt+R" })]
    [InlineData("Tab, Shift+Tab", new[] { "Tab", "Shift+Tab" })]
    [InlineData("Alt+1, Alt+2, Alt+3", new[] { "Alt+1", "Alt+2", "Alt+3" })]
    [InlineData("Space, Enter, Del", new[] { "Space", "Enter", "Del" })]
    [InlineData("Enter, Esc", new[] { "Enter", "Esc" })]
    [InlineData("Shift+arrows", new[] { "Shift+Arrows" })]
    [InlineData("Arrows", new[] { "Arrows" })]
    public void EachAlternativeIsOneCap(string keys, string[] caps)
    {
        Assert.Equal(caps, KeyCaps.Of(keys));
    }

    [Fact]
    public void ASpokenNameJoinsTheAlternativesWithOr()
    {
        Assert.Equal("Alt+1, Alt+2 or Alt+3", KeyCaps.Spoken("Alt+1, Alt+2, Alt+3"));
        Assert.Equal("Enter or Esc", KeyCaps.Spoken("Enter, Esc"));
        Assert.Equal("Ctrl+F", KeyCaps.Spoken("Ctrl+F"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void NothingIsNoCaps(string keys)
    {
        Assert.Empty(KeyCaps.Of(keys));
    }
}
