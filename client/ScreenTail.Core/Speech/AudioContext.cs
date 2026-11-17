namespace ScreenTail.Core.Speech;

/// <summary>
/// How much of whisper.cpp's thirty-second window the encoder works through for one segment (ST-031).
///
/// whisper.cpp encodes a full thirty seconds (1,500 context positions) on every call, whatever the audio
/// holds, so a two-second remark cost the laptop as much as a fifteen-second explanation: 5.3 s of
/// processor against 7.1 s (laptop run 44014246194). Encoding only the part of the window the segment
/// fills, plus a margin, measured 6.9 s down to 3.0 s on ten seconds of speech.
///
/// <b>Off unless the service is started with <see cref="Variable"/> set.</b> A smaller context is a known
/// source of quality loss in whisper.cpp, and the only speech it has been checked on is Windows' own
/// synthetic voice, which is the easiest speech there is. It stays off until ST-027's recorded narration
/// shows the word error unchanged (owner, 2026-11-16). The switch exists so that comparison is one
/// environment variable on <c>--transcribe</c>.
///
/// Two sizes and the full window, so at most three whisper states are held while the model is loaded:
/// <see cref="Sizes"/> is the memory this costs, and a size per segment length would be a state each.
/// </summary>
public static class AudioContext
{
    /// <summary>Set to 1, true or on to size the context to each segment.</summary>
    public const string Variable = "SCREENTAIL_SPEECH_SIZED_CONTEXT";

    /// <summary>The whole thirty-second window: whisper.cpp's own context, and what is used when sizing is off.</summary>
    public const int Full = 1500;

    /// <summary>Context positions per second of audio: the full window over its thirty seconds.</summary>
    public const int PerSecond = Full / 30;

    /// <summary>
    /// Beyond the audio itself, so the last word is never at the edge of what is encoded. A second is
    /// fifty positions, several words of speech.
    /// </summary>
    public static TimeSpan Margin { get; } = TimeSpan.FromSeconds(1);

    /// <summary>The reduced sizes, smallest first: about 10 s and 20 s of audio.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [512, 1024];

    /// <summary>Whether sizing is switched on, from the service's environment.</summary>
    public static bool Enabled(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var value = environment(Variable)?.Trim();
        return value is not null
            && (value == "1"
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The context for <paramref name="audio"/>: the smallest of <see cref="Sizes"/> that holds it and the
    /// margin, or <see cref="Full"/> when none does. Never smaller than the audio needs.
    /// </summary>
    public static int For(TimeSpan audio)
    {
        var needed = (audio + Margin).TotalSeconds * PerSecond;
        foreach (var size in Sizes)
        {
            if (size >= needed)
            {
                return size;
            }
        }

        return Full;
    }
}
