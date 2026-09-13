using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// ST-070's "navigation, window size and theme persist". These are cosmetic settings, and the tests are
/// mostly about what happens when saving or loading goes wrong — because the moment preferences are
/// written is when the app is closing, which is also when a machine is most likely to be shutting down.
/// </summary>
public sealed class ShellPreferencesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void WhatWasSavedComesBack()
    {
        var store = Store();
        var preferences = new ShellPreferences
        {
            View = ShellView.History,
            Theme = AppTheme.Light,
            Width = 1_280,
            Height = 800,
            Maximised = true,
            HudX = 1_600,
            HudY = 40,
        };

        Assert.True(store.Save(preferences));

        Assert.Equal(preferences, Store().Load());
    }

    [Fact]
    public void HidingTheHudIsNotRemembered()
    {
        // INV-4 allows a session with no visible indicator only where "the user explicitly chose" it, and
        // Spec §5 S2 makes hiding the HUD a per-session choice. Persisting it would turn one right-click
        // into a standing tray-only mode across reboots — a recording with no indicator that nobody opted
        // into, which is the thing INV-4 exists to prevent. The type has nowhere to put it.
        Assert.DoesNotContain(
            typeof(ShellPreferences).GetProperties(),
            p => p.Name.Contains("Hidden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFirstRunGetsDefaultsRatherThanNothing()
    {
        var loaded = Store().Load();

        Assert.Equal(ShellView.Review, loaded.View);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(0, loaded.Width);
    }

    [Fact]
    public void ACorruptFileIsReplacedRatherThanStoppingTheApp()
    {
        // Preferences are a convenience. Failing to start because a JSON file is unreadable would turn a
        // cosmetic problem into "ScreenTail will not open" in the middle of a support call.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathTo(), "{ this is not json");

        var loaded = Store().Load();

        Assert.Equal(ShellView.Review, loaded.View);
        Assert.True(Store().Save(loaded));
        Assert.Equal(loaded, Store().Load());
    }

    [Fact]
    public void AFileFullOfSomethingElseDoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathTo(), "[1, 2, 3]");

        Assert.Equal(new ShellPreferences(), Store().Load());
    }

    [Fact]
    public void NoHalfWrittenFileIsEverLeftBehind()
    {
        // Written through a temporary file and moved over, so a reader during a save never sees a partial
        // one — and a crash mid-save leaves the previous settings intact rather than a broken file.
        var store = Store();
        store.Save(new ShellPreferences { Theme = AppTheme.Light });

        store.Save(new ShellPreferences { Theme = AppTheme.HighContrast });

        Assert.False(File.Exists(PathTo() + ".tmp"), "a temporary file was left behind");
        Assert.Equal(AppTheme.HighContrast, Store().Load().Theme);
    }

    [Fact]
    public void AnUnwritableLocationIsReportedRatherThanThrown()
    {
        // A roaming profile that is read-only, or a locked-down machine. The shell carries on with the
        // settings it has; it just cannot remember them next time.
        var store = new ShellPreferencesStore(Path.Combine(_dir, "nope", "\0", "preferences.json"));

        Assert.False(store.Save(new ShellPreferences()));
    }

    [Fact]
    public void ThemeIsWrittenAsAWordAndNotANumber()
    {
        // The file is one a person may open. "Light" survives a reordering of the enum; 1 does not.
        Store().Save(new ShellPreferences { Theme = AppTheme.Light, View = ShellView.Settings });

        var written = File.ReadAllText(PathTo());

        Assert.Contains("\"Light\"", written, StringComparison.Ordinal);
        Assert.Contains("\"Settings\"", written, StringComparison.Ordinal);
    }

    private ShellPreferencesStore Store() => new(PathTo());

    private string PathTo() => Path.Combine(_dir, "preferences.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
