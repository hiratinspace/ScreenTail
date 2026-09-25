using ScreenTail.Api.Publish;
using ScreenTail.Api.Summarize;

namespace ScreenTail.Api.Tests.Publish;

/// <summary>The article as it lands in the knowledge base (ST-096): the note as HTML, the ticket named, everything escaped.</summary>
public sealed class KbArticleFormatterTests
{
    [Fact]
    public void TheNoteBecomesHeadingsListsAndParagraphsWithTheTicketNamed()
    {
        var html = KbArticleFormatter.Html(Note(), ticketId: "48213", frameCount: 2);

        Assert.Contains("<h3>Problem</h3><p>Printer offline in reception.</p>", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Steps</h3><ol><li>Checked the spooler service; it was stopped.</li></ol>", html, StringComparison.Ordinal);
        Assert.Contains("<h3>Result</h3><p>Printing works again.</p>", html, StringComparison.Ordinal);
        Assert.Contains("<p>From ticket #48213. 2 screenshots are attached to this article.</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TextIsEscapedSoAScreenCannotWriteHtmlIntoTheKnowledgeBase()
    {
        // T7: text on a customer's screen steers the note. In a knowledge base it would also render.
        var note = Note() with { Problem = "Run <script>alert(1)</script> & see" };

        var html = KbArticleFormatter.Html(note, "1", 0);

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; see", html, StringComparison.Ordinal);
    }

    private static DraftJson Note() => new()
    {
        Problem = "Printer offline in reception.",
        Steps = [new DraftStepJson { Text = "Checked the spooler service; it was stopped.", Confidence = "high" }],
        Result = "Printing works again.",
        FollowUps = [],
        SuggestedTitle = "Printer offline — Acme Dental",
        SuggestedTimeMinutes = 23,
        KbCandidate = true,
        KbReason = "Recurring",
        Source = "cloud",
        PromptVersion = "note_v1",
    };
}
