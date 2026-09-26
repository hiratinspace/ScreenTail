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
public sealed class StyleSignalsTests : IDisposable
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
        // The wire carries ids, never sentences, so a hint cannot become a prompt of its own.
        Assert.Equal(["past_tense", "shorter_steps", "no_full_stops", "sentence_case", "fewer_steps"], StyleSignals.HintIds);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
