using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Summarize;

/// <summary>
/// The style hints the backend knows, bound to the ones the client can send (ST-067; 2026-10-12 review,
/// T-105).
///
/// The client's StyleSignalsTests reads this side's list; this reads the client's. Each side's own suite
/// therefore fails when its change leaves the other behind, so a backend-only change (a hint added or
/// removed, a lower limit) cannot merge green because the client's job did not run. A bundle with a hint
/// this side does not know, or with more than <see cref="StyleHints.Max"/>, is refused, and a refused
/// bundle is refused again on every retry, so drift here fails every draft for each technician who had
/// learned the hint.
/// </summary>
public sealed partial class StyleHintsBindingTests
{
    [Fact]
    public void EveryHintTheClientCanSendIsKnownHereAndWithinTheLimit()
    {
        var source = File.ReadAllText(ClientStyleSignals());
        var list = ClientHintList().Match(source);
        Assert.True(list.Success, "StyleSignals.HintIds was not found in the client's source; the backend's list is bound to it.");
        var client = HintId().Matches(list.Groups["ids"].Value).Select(m => m.Groups["id"].Value).ToList();

        Assert.NotEmpty(client);
        Assert.All(client, id => Assert.True(StyleHints.IsKnown(id), $"the client can send \"{id}\" and the backend refuses it"));
        Assert.True(client.Count <= StyleHints.Max, string.Create(CultureInfo.InvariantCulture, $"the client can send {client.Count} hints and the backend takes {StyleHints.Max}"));
        Assert.Equal(Known().Order(StringComparer.Ordinal), client.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The ids this side has a sentence for. Private on purpose (the sentences are the prompt), so read
    /// here by name; a rename fails this test rather than passing it.
    /// </summary>
    private static IEnumerable<string> Known()
    {
        var field = typeof(StyleHints).GetField("Sentences", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("StyleHints.Sentences was renamed; update the binding test with it.");
        var sentences = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(field.GetValue(null));
        return sentences.Keys;
    }

    /// <summary>The client's list, found from the test's own folder up to the repository root.</summary>
    private static string ClientStyleSignals()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "client", "ScreenTail.Core", "Intel", "StyleSignals.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Failed rather than skipped: a list that moved is a binding that no longer holds.
        throw new FileNotFoundException("The client's StyleSignals.cs was not found above the test's folder; the backend's hint list is bound to it.");
    }

    [GeneratedRegex(@"\bHintIds\s*\{\s*get;\s*\}\s*=\s*\[(?<ids>[^\]]*)\]")]
    private static partial Regex ClientHintList();

    [GeneratedRegex(@"""(?<id>[a-z_]+)""")]
    private static partial Regex HintId();
}
