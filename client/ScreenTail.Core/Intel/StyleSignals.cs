using System.Text.Json;
using ScreenTail.Core.Store;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Core.Intel;

/// <summary>A structural change the technician made to a draft before publishing it. Never a word of the note.</summary>
public enum StyleSignal
{
    /// <summary>Steps rewritten from the imperative or present into the past tense.</summary>
    PastTense,

    /// <summary>Steps cut to well under their drafted length.</summary>
    ShorterSteps,

    /// <summary>Full stops removed from the ends of steps.</summary>
    NoFullStops,

    /// <summary>Steps capitalised at the start.</summary>
    SentenceCase,

    /// <summary>Steps merged or dropped: noticeably fewer than drafted.</summary>
    FewerSteps,
}

/// <summary>
/// What the diff between the draft as written and as published says about how this technician
/// writes (ST-067). Structural signals only, read off the shape of the steps rather than their words,
/// so nothing here is content (AC2): whether the first word went into the past tense, whether the
/// steps got shorter, lost their full stops, gained a capital, or were merged.
/// </summary>
public static class StyleSignals
{
    /// <summary>The ids the wire carries, in the order the backend lists them. Ids, never sentences.</summary>
    public static IReadOnlyList<string> HintIds { get; } = ["past_tense", "shorter_steps", "no_full_stops", "sentence_case", "fewer_steps"];

    private static readonly HashSet<string> IrregularPast = new(StringComparer.OrdinalIgnoreCase)
    {
        "ran", "set", "reset", "sent", "found", "told", "made", "took", "gave", "put", "saw", "went", "did",
        "got", "left", "kept", "let", "read", "reinstalled", "rebooted", "began", "brought", "built", "came",
        "chose", "cut", "dealt", "drove", "fixed", "held", "hit", "led", "lost", "met", "paid", "said", "shut",
        "spoke", "stood", "taught", "thought", "understood", "wrote", "was", "were", "had",
    };

    public static IReadOnlyList<StyleSignal> Observe(DraftNote original, DraftNote published)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(published);
        var before = original.Steps.Select(s => s.Text.Trim()).Where(t => t.Length > 0).ToList();
        var after = published.Steps.Select(s => s.Text.Trim()).Where(t => t.Length > 0).ToList();
        if (before.Count == 0 || after.Count == 0)
        {
            return [];
        }

        var signals = new List<StyleSignal>();
        var pairs = Math.Min(before.Count, after.Count);

        var intoPast = 0;
        var changed = 0;
        for (var i = 0; i < pairs; i++)
        {
            if (string.Equals(before[i], after[i], StringComparison.Ordinal))
            {
                continue;
            }

            changed++;
            if (!IsPast(FirstWord(before[i])) && IsPast(FirstWord(after[i])))
            {
                intoPast++;
            }
        }

        if (changed > 0 && intoPast * 2 >= changed && intoPast >= 1)
        {
            signals.Add(StyleSignal.PastTense);
        }

        var wordsBefore = before.Average(Words);
        var wordsAfter = after.Average(Words);
        if (wordsAfter <= wordsBefore * 0.7 && wordsBefore - wordsAfter >= 2)
        {
            signals.Add(StyleSignal.ShorterSteps);
        }

        if (Share(before, t => t.EndsWith('.')) >= 0.5 && Share(after, t => t.EndsWith('.')) <= 0.2)
        {
            signals.Add(StyleSignal.NoFullStops);
        }

        if (Share(before, t => char.IsLower(t[0])) >= 0.5 && Share(after, t => char.IsUpper(t[0])) >= 0.8)
        {
            signals.Add(StyleSignal.SentenceCase);
        }

        if (before.Count >= 3 && after.Count <= before.Count * 0.7)
        {
            signals.Add(StyleSignal.FewerSteps);
        }

        return signals;
    }

    public static string Id(StyleSignal signal) => signal switch
    {
        StyleSignal.PastTense => "past_tense",
        StyleSignal.ShorterSteps => "shorter_steps",
        StyleSignal.NoFullStops => "no_full_stops",
        StyleSignal.SentenceCase => "sentence_case",
        StyleSignal.FewerSteps => "fewer_steps",
        _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null),
    };

    private static string FirstWord(string text)
    {
        var end = 0;
        while (end < text.Length && char.IsLetter(text[end]))
        {
            end++;
        }

        return text[..end];
    }

    private static bool IsPast(string word) =>
        word.Length > 0 && (IrregularPast.Contains(word) || (word.EndsWith("ed", StringComparison.OrdinalIgnoreCase) && word.Length > 3));

    private static int Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static double Share(List<string> steps, Func<string, bool> test) => steps.Count == 0 ? 0 : steps.Count(test) / (double)steps.Count;
}

/// <summary>
/// The last ten sessions' signals, on disk beside the store, and the hints they earn (ST-067): a
/// signal seen in five of the last ten sessions becomes a hint on the next draft (AC1). Counts and
/// ids only; the file never holds a word of a note.
/// </summary>
public sealed class StyleStore(string path)
{
    public const int Window = 10;

    public const int Threshold = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private Document _document = Load(path);

    public int Sessions => _document.Sessions;

    public void Record(IReadOnlyList<StyleSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var recent = new List<string[]>(_document.Recent) { signals.Select(StyleSignals.Id).ToArray() };
        while (recent.Count > Window)
        {
            recent.RemoveAt(0);
        }

        _document = new Document(_document.Sessions + 1, recent);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_document, Json));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The signal still counts for this run; the file catches up on the next publish.
        }
    }

    /// <summary>The hint ids to send with the next draft, in the backend's order.</summary>
    public IReadOnlyList<string> Hints() =>
        [.. StyleSignals.HintIds.Where(id => _document.Recent.Count(session => session.Contains(id, StringComparer.Ordinal)) >= Threshold)];

    private static Document Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Json) ?? new Document(0, []) : new Document(0, []);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Document(0, []);
        }
    }

    private sealed record Document(int Sessions, IReadOnlyList<string[]> Recent);
}

/// <summary>Told when a session was published; reads the draft as written and as published and records what changed.</summary>
public interface IStyleLearner
{
    Task LearnAsync(string sessionId, CancellationToken ct = default);
}

public sealed class StyleLearner(StyleStore style, ISessionStore store) : IStyleLearner
{
    private readonly StyleStore _style = style ?? throw new ArgumentNullException(nameof(style));
    private readonly ISessionStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task LearnAsync(string sessionId, CancellationToken ct = default)
    {
        var session = await _store.LoadSessionAsync(sessionId, ct).ConfigureAwait(false);
        var original = await _store.LoadOriginalDraftAsync(sessionId, ct).ConfigureAwait(false);
        if (session?.Draft is { } published && original is not null)
        {
            _style.Record(StyleSignals.Observe(original, published));
        }
    }
}
