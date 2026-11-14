using ScreenTail.Core.Shell;

namespace ScreenTail.Core.Settings;

/// <summary>
/// Settings, Appearance (ST-070): System, Light or Dark, held for the page's one Save bar like every
/// other section, and applied when it is saved. The choice lives in the shell's own preferences file,
/// not with the capture service: it is how this window looks for this person, and nothing else reads it.
/// </summary>
public sealed class AppearanceSection : ISettingsSection
{
    public const string Theme = "Theme";

    /// <summary>The words a failed write puts on the bar, in the What happened. What to do. pattern.</summary>
    public const string NotSaved = "Windows did not let ScreenTail save the theme on this PC. Try again.";

    private readonly Func<ThemeChoice, bool> _save;

    /// <param name="saved">The choice in force when Settings opened.</param>
    /// <param name="save">Writes and applies a choice; false when it could not be written.</param>
    public AppearanceSection(ThemeChoice saved, Func<ThemeChoice, bool> save)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        Saved = ThemeChoices.Known(saved);
    }

    public string Name => "Appearance";

    /// <summary>The choice in force: what was last saved.</summary>
    public ThemeChoice Saved { get; private set; }

    /// <summary>The choice made and not yet saved, or null.</summary>
    public ThemeChoice? Pending { get; private set; }

    /// <summary>What the radios show: the pending choice, or the one in force.</summary>
    public ThemeChoice Shown => Pending ?? Saved;

    public IReadOnlyList<string> Changes => Pending is null ? [] : [Theme];

    public string? Problem => null;

    /// <summary>Holds <paramref name="choice"/> for the next Save; choosing what is in force again clears the change.</summary>
    public void Choose(ThemeChoice choice)
    {
        choice = ThemeChoices.Known(choice);
        Pending = choice == Saved ? null : choice;
    }

    public Task<string?> SaveAsync(CancellationToken ct = default)
    {
        if (Pending is not { } pending)
        {
            return Task.FromResult<string?>(null);
        }

        if (!_save(pending))
        {
            return Task.FromResult<string?>(NotSaved);
        }

        Saved = pending;
        Pending = null;
        return Task.FromResult<string?>(null);
    }

    public void Undo() => Pending = null;

    /// <summary>
    /// Writes <paramref name="choice"/> into <paramref name="store"/>, keeping everything else the file
    /// holds (the window's size, the view, the HUD's place). False when it could not be written.
    /// </summary>
    public static bool Persist(ShellPreferencesStore store, ThemeChoice choice)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.Save(store.Load() with { Appearance = choice });
    }
}
