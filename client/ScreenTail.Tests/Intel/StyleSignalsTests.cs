using System.Text.RegularExpressions;
using ScreenTail.Core.Intel;
using ScreenTail.Shared.Schema;
using static ScreenTail.Tests.Review.ReviewFixture;

namespace ScreenTail.Tests.Intel;

/// <summary>
/// ST-067: what the technician keeps changing about drafts, learned from the diff at publish and
/// handed back to the model as hints. Structural signals only — tense, length, punctuation, case —
/// never a word of the note (AC2); a hint only once the same change has been made in five of the
/// last ten sessions (AC1), so one unusual ticket does not steer the next.
/// </summary>
public sealed partial class StyleSignalsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RewritingIntoThePastTenseIsSeenAsATenseSignal()
    {
        var original = Draft(Step("Restart the print spooler."), Step("Clear the queue."), Step("Test a print from Word."));
        var published = Draft(Step("Restarted the print spooler."), Step("Cleared the queue."), Step("Tested a print from Word."));

        var observed = StyleSignals.Observe(original, published);

        Assert.Contains(StyleSignal.PastTense, observed);
        Assert.DoesNotContain(StyleSignal.ShorterSteps, observed);
    }

    [Fact]
    public void CuttingEveryStepDownAndDroppingFullStopsAreSeenToo()
    {
        var original = Draft(Step("Restarted the print spooler service from the Services console after confirming it had stopped."), Step("Cleared the stuck jobs out of the queue folder."));
        var published = Draft(Step("Restarted the spooler"), Step("Cleared the queue"));

        var observed = StyleSignals.Observe(original, published);

        Assert.Contains(StyleSignal.ShorterSteps, observed);
        Assert.Contains(StyleSignal.NoFullStops, observed);
    }

    [Fact]
    public void ADraftPublishedAsItCameSaysNothing()
    {
        var draft = Draft(Step("Restarted the print spooler."), Step("Cleared the queue."));

        Assert.Empty(StyleSignals.Observe(draft, draft));
    }

    [Fact]
    public void FiveConsistentRewritesEarnAHintOnTheSixthAndOneOffsDoNot()
    {
        // AC1: consistent past-tense rewrites over 5 sessions → tense hint on the 6th.
        var store = new StyleStore(Path.Combine(_dir, "style.json"));
        for (var i = 0; i < 4; i++)
        {
            store.Record([StyleSignal.PastTense]);
            Assert.Empty(store.Hints());
        }

        store.Record([StyleSignal.PastTense, StyleSignal.NoFullStops]);

        Assert.Equal(["past_tense"], store.Hints());
        Assert.Equal(5, new StyleStore(Path.Combine(_dir, "style.json")).Sessions);
    }

    [Fact]
    public void OnlyTheLastTenSessionsCount()
    {
        var store = new StyleStore(Path.Combine(_dir, "style.json"));
        for (var i = 0; i < 5; i++)
        {
            store.Record([StyleSignal.PastTense]);
        }

        for (var i = 0; i < 10; i++)
        {
            store.Record([]);
        }

        Assert.Empty(store.Hints());
    }

    [Fact]
    public void TheStoreHoldsCountsAndNeverAWordOfANote()
    {
        var store = new StyleStore(Path.Combine(_dir, "style.json"));
        var original = Draft(Step("Restart the spooler for Acme Dental ticket 48213."));
        var published = Draft(Step("Restarted the spooler for Acme Dental ticket 48213."));

        store.Record(StyleSignals.Observe(original, published));

        var file = File.ReadAllText(Path.Combine(_dir, "style.json"));
        Assert.DoesNotContain("Acme", file, StringComparison.Ordinal);
        Assert.DoesNotContain("48213", file, StringComparison.Ordinal);
        Assert.DoesNotContain("spooler", file, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("past_tense", file, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryHintTheClientCanSendIsOneTheBackendKnows()
    {
        // The wire carries ids, never sentences, so a hint cannot become a prompt of its own. The backend
        // refuses a bundle with an id it does not know, or with more hints than its limit, and a refused
        // bundle is refused again on every retry: a sixth hint added here alone would fail every draft for
        // each technician who had learned it (2026-10-12 review, T-105). So this reads the backend's own
        // list rather than a copy of it, and the two cannot drift apart without this going red.
        var source = File.ReadAllText(BackendStyleHints());
        var known = BackendHintId().Matches(source).Select(m => m.Groups["id"].Value).ToList();
        var max = int.Parse(BackendMax().Match(source).Groups["max"].Value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.NotEmpty(known);
        Assert.Equal(known.Order(StringComparer.Ordinal), StyleSignals.HintIds.Order(StringComparer.Ordinal));
        Assert.True(StyleSignals.HintIds.Count <= max, $"the client can send {StyleSignals.HintIds.Count} hints and the backend takes {max}");
        Assert.All(StyleSignals.HintIds, id => Assert.Matches("^[a-z_]+$", id));
    }

    /// <summary>The backend's list, found from the test's own folder up to the repository root.</summary>
    private static string BackendStyleHints()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "src", "ScreenTail.Api", "Summarize", "StyleHints.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Failed rather than skipped: a list that moved is a binding that no longer holds.
        throw new FileNotFoundException("The backend's StyleHints.cs was not found above the test's folder; the client's hint list is bound to it.");
    }

    [GeneratedRegex(@"\[""(?<id>[a-z_]+)""\]\s*=")]
    private static partial Regex BackendHintId();

    [GeneratedRegex(@"const\s+int\s+Max\s*=\s*(?<max>\d+)\s*;")]
    private static partial Regex BackendMax();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
