using System.Text.Json;
using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// Spec v0.6.0 §3 Frame (built v0.6.12): the title bar takes the frame's colours, read from tokens.json,
/// in dark and light, and the system's own in high contrast.
/// </summary>
public sealed class TitleBarTintTests
{
    private static readonly JsonDocument Tokens = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Design", "tokens.json")));

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void TheTitleBarIsTheFrame(string theme)
    {
        var frame = Token("shell.bg", theme);
        var text = Token("shell.text", theme);

        var tint = TitleBarTint.For(highContrast: false, frame, text);

        Assert.Equal(TitleBarTint.ColorRef(frame), tint.Caption);
        Assert.Equal(TitleBarTint.ColorRef(frame), tint.Border);
        Assert.Equal(TitleBarTint.ColorRef(text), tint.Text);

        // Dark caption buttons (drawn light) on the dark frame; on the light frame (v0.7.0, a soft grey panel)
        // Windows' own dark-on-light buttons.
        Assert.Equal(theme == "dark", tint.DarkCaptionButtons);
    }

    [Fact]
    public void HighContrastKeepsTheSystemsTitleBar()
    {
        var tint = TitleBarTint.For(highContrast: true, "#141030", "#F7F5FE");

        Assert.Equal(TitleBarTint.SystemDefault, tint.Caption);
        Assert.Equal(TitleBarTint.SystemDefault, tint.Text);
        Assert.Equal(TitleBarTint.SystemDefault, tint.Border);
        Assert.False(tint.DarkCaptionButtons);
    }

    [Fact]
    public void AColourIsSentRedInTheLowByte()
    {
        // COLORREF is 0x00BBGGRR: the light frame #141030 is 0x00301014.
        Assert.Equal(0x00301014u, TitleBarTint.ColorRef("#141030"));
        Assert.Equal(0x00FEF5F7u, TitleBarTint.ColorRef("#F7F5FE"));
        Assert.Throws<FormatException>(() => TitleBarTint.ColorRef("141030"));
    }

    [Fact]
    public void ALightFrameWouldTakeDarkCaptionButtons()
    {
        Assert.False(TitleBarTint.For(highContrast: false, "#F7F5FE", "#141030").DarkCaptionButtons);
    }

    private static string Token(string name, string theme) =>
        Tokens.RootElement.GetProperty("color").GetProperty(name).GetProperty(theme).GetString()!;
}
