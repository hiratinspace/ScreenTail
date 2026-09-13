using System.Numerics;

namespace ScreenTail.Core.Capture;

/// <summary>
/// A fingerprint of what was on screen, used to tell "nothing happened" from "something did" (ST-026).
/// </summary>
public readonly struct SceneHash : IEquatable<SceneHash>
{
    /// <summary>Comparisons the fingerprint is made of: one per neighbouring pair, across and down.</summary>
    public const int Bits = ((PerceptualHash.Columns - 1) * PerceptualHash.Rows)
        + (PerceptualHash.Columns * (PerceptualHash.Rows - 1));

    internal const int Words = 8;

    private readonly ulong _w0;
    private readonly ulong _w1;
    private readonly ulong _w2;
    private readonly ulong _w3;
    private readonly ulong _w4;
    private readonly ulong _w5;
    private readonly ulong _w6;
    private readonly ulong _w7;

    internal SceneHash(ReadOnlySpan<ulong> words)
    {
        _w0 = words[0];
        _w1 = words[1];
        _w2 = words[2];
        _w3 = words[3];
        _w4 = words[4];
        _w5 = words[5];
        _w6 = words[6];
        _w7 = words[7];
    }

    /// <summary>The fingerprint of a screen with nothing on it, and what an all-equal grid hashes to.</summary>
    public static SceneHash Blank => default;

    /// <summary>How many of the <see cref="Bits"/> comparisons came out differently. 0 is the same picture.</summary>
    public int DistanceTo(SceneHash other) =>
        BitOperations.PopCount(_w0 ^ other._w0)
        + BitOperations.PopCount(_w1 ^ other._w1)
        + BitOperations.PopCount(_w2 ^ other._w2)
        + BitOperations.PopCount(_w3 ^ other._w3)
        + BitOperations.PopCount(_w4 ^ other._w4)
        + BitOperations.PopCount(_w5 ^ other._w5)
        + BitOperations.PopCount(_w6 ^ other._w6)
        + BitOperations.PopCount(_w7 ^ other._w7);

    public bool Equals(SceneHash other) => DistanceTo(other) == 0;

    public override bool Equals(object? obj) => obj is SceneHash other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_w0, _w1, _w2, _w3, _w4, _w5, _w6, _w7);

    public static bool operator ==(SceneHash left, SceneHash right) => left.Equals(right);

    public static bool operator !=(SceneHash left, SceneHash right) => !left.Equals(right);

    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{_w0:x16}{_w1:x16}{_w2:x16}{_w3:x16}{_w4:x16}{_w5:x16}{_w6:x16}{_w7:x16}");
}

/// <summary>
/// The difference hash: each bit says whether one cell of the screen is brighter than the cell beside or
/// below it (ST-026).
///
/// Chosen over comparing frames directly, and over an average hash, for two properties. It ignores overall
/// brightness, because a screen dimming or a theme changing is not a new thing to photograph; and it
/// survives noise and the odd changed pixel, because a blinking caret or a ticking clock must not produce a
/// screenshot every second for twenty minutes.
///
/// <b>The grid size and the second direction were both measured, not chosen.</b> The textbook dHash is 9×8
/// and compares across only. On a 1920×1080 desktop that cannot see a dialog: a 400×300 one scores 3
/// against background noise that already scores 1, so no threshold separates them. Widening the grid to
/// 17×16 lifts dialogs to 7–12 against the same noise at 3. Adding the downward comparison — dHash is blind
/// along whichever axis it does not walk, so a wide uniform band changing brightness is invisible to the
/// horizontal one — lifts them again to 19–33, with the caret, the clock and the noise unmoved at 0, 0
/// and 3. Three times the signal for the same noise and one more loop.
///
/// <b>What it still cannot see:</b> a region that changes brightness while staying on the same side of
/// everything around it. A dark log pane going from grey to darker grey is invisible to any hash built on
/// orderings. That is a deliberate trade for the noise immunity, and clicks still photograph what the
/// technician actually does. <see cref="ScreenTail.Tests.Capture.SceneGridTests"/> holds all of these as
/// assertions so a change that closes the gap fails rather than quietly stopping the feature from noticing
/// anything.
/// </summary>
public static class PerceptualHash
{
    public const int Columns = 17;

    public const int Rows = 16;

    /// <summary>Bytes a grid must contain: <see cref="Columns"/> × <see cref="Rows"/>, row-major.</summary>
    public const int GridLength = Columns * Rows;

    /// <param name="grayscale">A <see cref="Columns"/>×<see cref="Rows"/> grid, one byte of brightness per cell.</param>
    public static SceneHash OfGrid(ReadOnlySpan<byte> grayscale)
    {
        if (grayscale.Length != GridLength)
        {
            throw new ArgumentException($"A scene grid is {GridLength} bytes ({Columns}x{Rows}).", nameof(grayscale));
        }

        Span<ulong> words = stackalloc ulong[SceneHash.Words];
        words.Clear();
        var bit = 0;

        // Across: is this cell brighter than the one to its right?
        for (var row = 0; row < Rows; row++)
        {
            var start = row * Columns;
            for (var column = 0; column < Columns - 1; column++)
            {
                Set(words, ref bit, grayscale[start + column] > grayscale[start + column + 1]);
            }
        }

        // Down: is this cell brighter than the one below it?
        for (var column = 0; column < Columns; column++)
        {
            for (var row = 0; row < Rows - 1; row++)
            {
                Set(words, ref bit, grayscale[(row * Columns) + column] > grayscale[((row + 1) * Columns) + column]);
            }
        }

        return new SceneHash(words);
    }

    private static void Set(Span<ulong> words, ref int bit, bool value)
    {
        if (value)
        {
            words[bit >> 6] |= 1UL << (bit & 63);
        }

        bit++;
    }
}
