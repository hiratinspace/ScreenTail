namespace ScreenTail.Core.Shell;

/// <summary>
/// How the shortcut sheet draws a line's keys (Spec §4, v0.6.11): each alternative ("Enter, Esc") is one
/// key cap, and a chord ("Ctrl+Alt+R") stays whole in its cap so it reads as keys pressed together.
/// </summary>
public static class KeyCaps
{
    /// <summary>The caps for <paramref name="keys"/>, as the sheet's line writes them ("Tab, Shift+Tab").</summary>
    public static IReadOnlyList<string> Of(string keys) =>
        string.IsNullOrWhiteSpace(keys)
            ? []
            : [.. keys.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Cap)];

    /// <summary>The keys as a screen reader says them: the alternatives joined with "or".</summary>
    public static string Spoken(string keys)
    {
        var caps = Of(keys);
        return caps.Count switch
        {
            0 => string.Empty,
            1 => caps[0],
            _ => string.Join(", ", caps.Take(caps.Count - 1)) + " or " + caps[^1],
        };
    }

    /// <summary>Each key of a chord starts with a capital ("Shift+arrows" is drawn "Shift+Arrows").</summary>
    private static string Cap(string chord) =>
        string.Join('+', chord.Split('+').Select(key => key.Length > 1 && char.IsLower(key[0]) ? char.ToUpperInvariant(key[0]) + key[1..] : key));
}
