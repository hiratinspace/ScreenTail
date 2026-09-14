namespace ScreenTail.Core.Review;

/// <summary>
/// The gate in front of an irreversible action (Spec §3, §5 S3 Discard, §5 S4 bulk discard).
///
/// A typed confirmation exists to cost a second of attention, not to be guessable, so the match is exact
/// after trimming the whitespace a paste brings with it. Accepting "discard" would let the muscle memory
/// that types lowercase everything sail straight through, which is the memory the gate is there to
/// interrupt; and the word is on screen, so typing it exactly asks nothing of anyone.
///
/// Separate from the dialog because it is the rule, and because §5 S4 confirms a bulk discard with the
/// number of selected sessions instead of a word (v0.4.1 Q6) — the same rule, a different token.
/// </summary>
public sealed class TypedConfirmation(string expected)
{
    /// <summary>Spec §5 S3: Discard in Review is confirmed by typing DISCARD.</summary>
    public const string DiscardWord = "DISCARD";

    private readonly string _expected = string.IsNullOrWhiteSpace(expected)
        ? throw new ArgumentException("A confirmation with nothing to type confirms nothing.", nameof(expected))
        : expected;

    /// <summary>Spec §5 S4: bulk discard is confirmed by typing how many sessions are selected.</summary>
    public static TypedConfirmation ForCount(int sessions) => sessions > 0
        ? new TypedConfirmation(sessions.ToString(System.Globalization.CultureInfo.InvariantCulture))
        : throw new ArgumentOutOfRangeException(nameof(sessions), sessions, "Nothing is selected.");

    public static TypedConfirmation ForDiscard() => new(DiscardWord);

    /// <summary>What the dialog shows: "Type DISCARD to confirm".</summary>
    public string Prompt => $"Type {_expected} to confirm";

    public bool Accepts(string? typed) =>
        typed is not null && string.Equals(typed.Trim(), _expected, StringComparison.Ordinal);
}
