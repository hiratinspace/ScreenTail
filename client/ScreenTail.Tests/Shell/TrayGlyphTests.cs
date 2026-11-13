using System.Security.Cryptography;
using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// ST-016: the tray and app icons are the brand's (Spec v0.6.0 §2.0), one per tray state and taskbar.
///
/// The tray icon is the one capture indicator that is always on screen (INV-4), so each state has to come
/// out as its own picture on both taskbars, at every scale Windows asks for. These hold the shipped .ico
/// files to that on any OS; the Windows suite holds the tray to loading them.
/// </summary>
public sealed class TrayGlyphTests
{
    private static readonly int[] TraySizes = [16, 20, 24, 32, 40, 48];
    private static readonly int[] AppSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    private static string Brand => Path.Combine(AppContext.BaseDirectory, "Brand");

    [Fact]
    public void EveryStateAndTaskbarHasItsOwnFileName()
    {
        var names = Enum.GetValues<TrayIcon>()
            .SelectMany(state => new[] { TrayGlyph.FileName(state, true), TrayGlyph.FileName(state, false) })
            .ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("tray-recording-light.ico", TrayGlyph.FileName(TrayIcon.Recording, lightTaskbar: true));
        Assert.Equal("tray-offline-dark.ico", TrayGlyph.FileName(TrayIcon.Offline, lightTaskbar: false));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    [InlineData("1", false)]
    [InlineData(2, false)]
    public void OnlySystemUsesLightThemeOneIsALightTaskbar(object? value, bool light) =>
        Assert.Equal(light, TrayGlyph.IsLightTaskbar(value));

    [Theory]
    [InlineData(1, false, false, true)]
    [InlineData(0, false, true, false)]
    [InlineData(1, true, false, false)]
    [InlineData(0, true, true, true)]
    [InlineData(null, true, true, true)]
    public void InHighContrastTheTaskbarIsTheWindowColour(object? systemUsesLightTheme, bool highContrast, bool windowIsLight, bool light)
    {
        // A high contrast theme draws the taskbar in its window colour (black in Night Sky, white in
        // Desert) whatever SystemUsesLightTheme says, so the glyph is picked by that colour (v0.6.10).
        Assert.Equal(light, TrayGlyph.IsLightTaskbar(systemUsesLightTheme, highContrast, windowIsLight));
    }

    [Fact]
    public void EachTrayStateShipsADistinctIconWithEveryScale()
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in Enum.GetValues<TrayIcon>())
        {
            foreach (var light in new[] { true, false })
            {
                var name = TrayGlyph.FileName(state, light);
                var bytes = File.ReadAllBytes(Path.Combine(Brand, "Tray", name));
                Assert.Equal(TraySizes, Sizes(bytes));
                hashes[name] = Convert.ToHexString(SHA256.HashData(bytes));
            }
        }

        // Ten files, ten pictures: no state borrows another's icon on either taskbar.
        Assert.Equal(10, hashes.Count);
        Assert.Equal(hashes.Count, hashes.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheAppIconCarriesEverySizeTheSpecNames()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Brand, "app.ico"));
        Assert.Equal(AppSizes, Sizes(bytes));
    }

    [Fact]
    public void NoTrayFileIsLeftOverFromAStateThatDoesNotExist()
    {
        var expected = Enum.GetValues<TrayIcon>()
            .SelectMany(state => new[] { TrayGlyph.FileName(state, true), TrayGlyph.FileName(state, false) })
            .Order(StringComparer.Ordinal);
        var shipped = Directory.GetFiles(Path.Combine(Brand, "Tray"), "*.ico").Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(expected, shipped);
    }

    /// <summary>The frame sizes in an .ico's directory, smallest first. A width byte of 0 means 256.</summary>
    private static int[] Sizes(byte[] ico)
    {
        Assert.Equal(0, BitConverter.ToUInt16(ico, 0));
        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
        var count = BitConverter.ToUInt16(ico, 4);
        var sizes = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (16 * i);
            var width = ico[entry] == 0 ? 256 : ico[entry];
            var height = ico[entry + 1] == 0 ? 256 : ico[entry + 1];
            Assert.Equal(width, height);
            Assert.Equal(32, BitConverter.ToUInt16(ico, entry + 6));
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            Assert.True(offset + length <= ico.Length, $"Frame {width} runs past the end of the file.");
            sizes.Add(width);
        }

        return [.. sizes.Order()];
    }
}
