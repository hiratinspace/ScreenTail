using ScreenTail.Core.Settings;
using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Settings;

/// <summary>
/// Settings, Appearance (ST-070): System, Light or Dark on the page's one Save bar. Nothing changes on
/// screen until Save, as with every other section; a write that fails says so on the bar.
/// </summary>
public sealed class AppearanceSectionTests
{
    [Theory]
    [InlineData(ThemeChoice.System, true, true)]
    [InlineData(ThemeChoice.System, false, false)]
    [InlineData(ThemeChoice.Light, false, true)]
    [InlineData(ThemeChoice.Dark, true, false)]
    public void SystemFollowsWindowsAndTheOthersDoNot(ThemeChoice choice, bool windowsUsesLight, bool light) =>
        Assert.Equal(light, ThemeChoices.UsesLight(choice, windowsUsesLight));

    [Fact]
    public void AValueTheFileShouldNotHoldIsReadAsSystem()
    {
        var section = new AppearanceSection((ThemeChoice)42, _ => true);

        Assert.Equal(ThemeChoice.System, section.Shown);
        Assert.Empty(section.Changes);
    }

    [Fact]
    public void AChoiceIsHeldUntilSaveAndOnlyThenWritten()
    {
        var written = new List<ThemeChoice>();
        var section = new AppearanceSection(ThemeChoice.System, c =>
        {
            written.Add(c);
            return true;
        });

        section.Choose(ThemeChoice.Light);

        Assert.Equal([AppearanceSection.Theme], section.Changes);
        Assert.Equal(ThemeChoice.Light, section.Shown);
        Assert.Empty(written);
    }

    [Fact]
    public async Task SaveWritesTheChoiceAndClearsTheChange()
    {
        var written = new List<ThemeChoice>();
        var section = new AppearanceSection(ThemeChoice.System, c =>
        {
            written.Add(c);
            return true;
        });
        section.Choose(ThemeChoice.Dark);

        Assert.Null(await section.SaveAsync());

        Assert.Equal([ThemeChoice.Dark], written);
        Assert.Empty(section.Changes);
        Assert.Equal(ThemeChoice.Dark, section.Saved);
        Assert.Equal(ThemeChoice.Dark, section.Shown);
    }

    [Fact]
    public void ChoosingWhatIsInForceAgainIsNotAChange()
    {
        var section = new AppearanceSection(ThemeChoice.Dark, _ => true);
        section.Choose(ThemeChoice.Light);

        section.Choose(ThemeChoice.Dark);

        Assert.Empty(section.Changes);
    }

    [Fact]
    public async Task NothingIsWrittenWhenNothingChanged()
    {
        var calls = 0;
        var section = new AppearanceSection(ThemeChoice.Light, _ =>
        {
            calls++;
            return true;
        });

        Assert.Null(await section.SaveAsync());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AWriteThatFailsIsSaidOnTheBarAndTheChoiceStaysPending()
    {
        var section = new AppearanceSection(ThemeChoice.System, _ => false);
        section.Choose(ThemeChoice.Light);

        Assert.Equal(AppearanceSection.NotSaved, await section.SaveAsync());

        Assert.Equal([AppearanceSection.Theme], section.Changes);
        Assert.Equal(ThemeChoice.System, section.Saved);
    }

    [Fact]
    public void UndoPutsBackWhatIsInForce()
    {
        var section = new AppearanceSection(ThemeChoice.Dark, _ => true);
        section.Choose(ThemeChoice.System);

        section.Undo();

        Assert.Empty(section.Changes);
        Assert.Equal(ThemeChoice.Dark, section.Shown);
    }

    [Fact]
    public async Task ThePageCountsItAndSavesItWithTheOthers()
    {
        var section = new AppearanceSection(ThemeChoice.System, _ => true);
        var page = new SettingsPage([section]);
        section.Choose(ThemeChoice.Light);

        Assert.Equal(1, page.PendingIn("Appearance"));
        Assert.True(await page.SaveAsync());
        Assert.Equal(0, page.PendingCount);
    }
}
