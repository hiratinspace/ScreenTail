using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTail.Core.Shell;

public enum AppTheme
{
    Dark,
    Light,
    HighContrast,
}

/// <param name="Width">Zero means "not set yet", so the shell picks its own size the first time.</param>
public sealed record ShellPreferences
{
    public ShellView View { get; init; } = ShellView.Review;

    public AppTheme Theme { get; init; } = AppTheme.Dark;

    public double Width { get; init; }

    public double Height { get; init; }

    public bool Maximised { get; init; }

    /// <summary>
    /// Whether the technician hid the HUD for this session (Spec §5 S2 right-click). Kept here rather than
    /// in the session, because it is a preference about the UI and not a fact about what was captured.
    /// </summary>
    public bool HudHidden { get; init; }
}

/// <summary>
/// Remembers where the shell was and how it looked (ST-070, Spec §4).
///
/// Written atomically through a temporary file, because the moment this is saved is when the app is
/// closing — which is also when a machine is most likely to be shutting down or losing power. A half-
/// written preferences file that fails to parse would reset a technician's window and theme every time
/// something went wrong, which looks exactly like the app losing their settings at random.
///
/// <b>A file that cannot be read is not an error.</b> Preferences are a convenience; failing to start the
/// UI because a JSON file is corrupt would turn a cosmetic problem into "ScreenTail will not open" in the
/// middle of a support call. A bad file is replaced by defaults and overwritten on the next save.
///
/// Nothing here is content (INV-10): a window size, a theme, and which of three views was open.
/// </summary>
public sealed class ShellPreferencesStore(string path)
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path => path;

    public ShellPreferences Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ShellPreferences>(File.ReadAllText(path), Format) ?? new ShellPreferences()
                : new ShellPreferences();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return new ShellPreferences();
        }
    }

    /// <summary>Returns false when the preferences could not be written. Never throws: this is cosmetic.</summary>
    public bool Save(ShellPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Written beside the real file and moved over it, so a reader never sees a partial one.
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, Format));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Everything a bad path or a bad file can throw.
    ///
    /// The list includes the ones a malformed path raises — a null character, a name too long, a shape
    /// Windows does not support — and not only the ones a failed write does. Those arrive from a roaming
    /// profile or a settings file just as easily as from a bug, and a method whose contract is "returns
    /// false, never throws" has to mean it: this is the one place in the app where failing loudly would
    /// turn a cosmetic problem into "ScreenTail will not open".
    /// </summary>
    private static bool IsExpected(Exception ex) =>
        ex is JsonException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;
}
