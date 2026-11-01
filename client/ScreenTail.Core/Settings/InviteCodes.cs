namespace ScreenTail.Core.Settings;

/// <summary>
/// The shape of an invite code as the backend issues it (ST-010; ADR-0009, "Guessing invite codes"),
/// copied by hand from the backend's <c>Invites</c> the way the wire contracts are. Twenty characters
/// from 31 with no look-alikes (no 0, 1, I, L or O), shown in four groups of five: about 99 bits, so
/// a code cannot be guessed however many machines try. Codes issued before 2026-11-01 were ten
/// characters and are accepted until they expire (72 hours at most), so the card takes those too.
/// </summary>
public static class InviteCodes
{
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const int Length = 20;

    public const int LegacyLength = 10;

    public const int GroupLength = 5;

    /// <summary>A made-up code of today's shape, for the card's hints. Not one anybody issued.</summary>
    public const string Example = "KX7PM-4R2WQ-9HTCE-B3NVA";

    /// <summary>The letters and digits of what was typed or pasted, in capitals: dashes, spaces and case are forgiven, as the backend forgives them.</summary>
    public static string Normalise(string? typed) =>
        new((typed ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Whether a normalised code has a code's length and only the alphabet's characters.</summary>
    public static bool IsComplete(string normalised) =>
        normalised is { Length: Length or LegacyLength } && normalised.All(c => Alphabet.Contains(c, StringComparison.Ordinal));

    /// <summary>Whether a normalised code holds a character no code has (a 0, 1, I, L, O or anything outside the alphabet).</summary>
    public static bool HasForeignCharacter(string normalised) =>
        normalised.Any(c => !Alphabet.Contains(c, StringComparison.Ordinal));

    /// <summary>What was typed, normalised and grouped in fives with dashes between, as the invite shows it.</summary>
    public static string Format(string? typed) =>
        string.Join('-', Normalise(typed).Chunk(GroupLength).Select(group => new string(group)));
}
