namespace ScreenTail.Api.Summarize;

/// <summary>
/// The style hints the client may send and what each says to the model (ST-067). A fixed list on this
/// side: the wire carries ids, so a client can steer the note's shape but never put a sentence of its
/// own into the prompt.
/// </summary>
public static class StyleHints
{
    public const int Max = 5;

    private static readonly Dictionary<string, string> Sentences = new(StringComparer.Ordinal)
    {
        ["past_tense"] = "Write every step in the past tense, as something already done.",
        ["shorter_steps"] = "Keep each step to one short sentence.",
        ["no_full_stops"] = "Do not end steps with a full stop.",
        ["sentence_case"] = "Start each step with a capital letter.",
        ["fewer_steps"] = "Prefer fewer, broader steps over many small ones.",
    };

    public static bool IsKnown(string id) => Sentences.ContainsKey(id);

    /// <summary>The paragraph for the model, or null when there are no hints.</summary>
    public static string? Paragraph(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var lines = ids.Where(Sentences.ContainsKey).Select(id => Sentences[id]).ToList();
        return lines.Count == 0
            ? null
            : "This technician usually edits drafts the same way, so write it that way to begin with:\n- " + string.Join("\n- ", lines);
    }
}
