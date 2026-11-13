using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// The words of every question the app asks in its one dialog (Spec §3 Modal, §4 copy voice, v0.6.11):
/// plain words, a button that says what happens ("Discard 2 sessions"), never "OK", "Yes" or "Confirm",
/// and the danger style on what destroys something.
/// </summary>
public sealed class ConfirmCopyTests
{
    public static TheoryData<string> Names => [.. Every().Keys];

    /// <summary>Words that say nothing about what happens.</summary>
    private static readonly string[] Empty = ["OK", "Ok", "Yes", "No", "Confirm"];

    private static Dictionary<string, ConfirmWords> Every() => new(StringComparer.Ordinal)
    {
        ["discard"] = ConfirmCopy.DiscardSession,
        ["bulk-1"] = ConfirmCopy.DiscardSessions(1),
        ["bulk-3"] = ConfirmCopy.DiscardSessions(3),
        ["erase"] = ConfirmCopy.DeleteEverything,
        ["mark"] = ConfirmCopy.MarkAsPublished("ScreenConnect session, started Tue 10 Nov 14:02, 9 min"),
        ["quit"] = ConfirmCopy.QuitWhileCapturing,
        ["remove"] = ConfirmCopy.RemoveIntegration("ConnectWise Manage"),
        ["not-discarded"] = ConfirmCopy.NotDiscarded("The capture service did not answer. Check that it is running, then try again."),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void TheButtonSaysWhatHappensAndNeverOk(string name)
    {
        var words = Every()[name];
        foreach (var button in new[] { words.Action, words.Cancel }.OfType<string>())
        {
            Assert.DoesNotContain(button, Empty);
            Assert.DoesNotContain('?', button);
            Assert.False(string.IsNullOrWhiteSpace(button));
        }

        Assert.NotEqual(words.Action, words.Cancel);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheWordsArePlain(string name)
    {
        var words = Every()[name];
        foreach (var text in new[] { words.Heading, words.Consequence, words.Action, words.Cancel }.OfType<string>())
        {
            Assert.DoesNotContain('—', text);
            Assert.DoesNotContain('!', text);
            Assert.DoesNotContain("Oops", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Are you sure", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(1, "Discard 1 session")]
    [InlineData(2, "Discard 2 sessions")]
    [InlineData(12, "Discard 12 sessions")]
    public void BulkDiscardNamesHowManyGo(int count, string action)
    {
        var words = ConfirmCopy.DiscardSessions(count);

        Assert.Equal(action, words.Action);
        Assert.Equal(action + "?", words.Heading);
        Assert.Equal(ConfirmTone.Danger, words.Tone);
    }

    [Fact]
    public void WhatDestroysSomethingIsDangerAndWhatDoesNotIsNot()
    {
        Assert.Equal(ConfirmTone.Danger, ConfirmCopy.DiscardSession.Tone);
        Assert.Equal(ConfirmTone.Danger, ConfirmCopy.DeleteEverything.Tone);
        Assert.Equal(ConfirmTone.Danger, ConfirmCopy.RemoveIntegration("Hudu").Tone);

        // Vouching for a ticket and quitting destroy nothing: the filled button is the ordinary one.
        Assert.Equal(ConfirmTone.Primary, ConfirmCopy.MarkAsPublished("ScreenConnect session, started Tue 10 Nov 14:02, 9 min").Tone);
        Assert.Equal(ConfirmTone.Primary, ConfirmCopy.QuitWhileCapturing.Tone);
    }

    [Fact]
    public void DiscardSaysWhatGoesAndThatItCannotBeUndone()
    {
        Assert.Equal("Discard session", ConfirmCopy.DiscardSession.Action);
        Assert.Contains("screenshots", ConfirmCopy.DiscardSession.Consequence, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", ConfirmCopy.DiscardSession.Consequence, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", ConfirmCopy.DiscardSessions(2).Consequence, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", ConfirmCopy.DeleteEverything.Consequence, StringComparison.Ordinal);
        Assert.Equal("Delete everything", ConfirmCopy.DeleteEverything.Action);
    }

    [Fact]
    public void MarkAsPublishedAsksInTheWordsOnTheRow()
    {
        // Spec §5 S4 (v0.5.3, worded v0.6.11): a short question, then the session in words (tool, start,
        // how long, never a bare "9:00" beside a time of day), then what follows. The way out says what
        // it means: "Not yet", since the technician may mark it once the ticket is checked.
        var words = ConfirmCopy.MarkAsPublished("ScreenConnect session, started Tue 10 Nov 14:02, 9 min");

        Assert.Equal("Mark this session as published?", words.Heading);
        Assert.Equal("ScreenConnect session, started Tue 10 Nov 14:02, 9 min.\n\n" + MarkAsPublished.Question, words.Consequence);
        Assert.Equal("Not yet", words.Cancel);
        Assert.Equal(MarkAsPublished.Action, words.Action);
        Assert.True(words.CancelIsDefault, "Enter must not vouch for a ticket by reflex.");
    }

    [Fact]
    public void DiscardSaysTheNoteGoesAndNothingIsSent()
    {
        // Spec v0.6.12 §4: the words say what happens. "The note goes with them" left the technician to
        // work out whether a note had gone to the ticket; nothing is sent, and the words now say so.
        foreach (var words in new[] { ConfirmCopy.DiscardSession, ConfirmCopy.DiscardSessions(1), ConfirmCopy.DiscardSessions(3) })
        {
            Assert.Contains("deleted from this device", words.Consequence, StringComparison.Ordinal);
            Assert.Contains("Nothing is sent to a ticket.", words.Consequence, StringComparison.Ordinal);
            Assert.DoesNotContain("goes with", words.Consequence, StringComparison.Ordinal);
        }

        Assert.Contains("draft note", ConfirmCopy.DiscardSession.Consequence, StringComparison.Ordinal);
        Assert.Contains("draft notes", ConfirmCopy.DiscardSessions(3).Consequence, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteEverythingSaysWhatGoesAndWhatStaysInPlainWords()
    {
        // "This device's tokens go" was jargon and did not say what happens to what is already on tickets.
        var words = ConfirmCopy.DeleteEverything.Consequence;

        Assert.DoesNotContain("token", words, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is deleted", words, StringComparison.Ordinal);
        Assert.Contains("this device's sign-in", words, StringComparison.Ordinal);
        Assert.Contains("Notes already published stay on their tickets.", words, StringComparison.Ordinal);
        Assert.Contains("set up again", words, StringComparison.Ordinal);
    }

    [Fact]
    public void QuitSaysTheSessionStopsAndItsNoteIsStillDrafted()
    {
        // "Quitting closes the recording indicator" was the reason inside the app, not what happens to the
        // technician's work: the session stops, and the capture service still drafts its note.
        var words = ConfirmCopy.QuitWhileCapturing.Consequence;

        Assert.DoesNotContain("indicator", words, StringComparison.Ordinal);
        Assert.Contains("stops it before quitting", words, StringComparison.Ordinal);
        Assert.Contains("its note is still drafted", words, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedDiscardSaysTheSessionIsStillThere()
    {
        var words = ConfirmCopy.NotDiscarded("Not connected to the capture service, so nothing was changed.");

        Assert.Equal("Not connected to the capture service, so nothing was changed. The session is still on this device.", words.Consequence);
    }

    [Fact]
    public void RemovingAnIntegrationNamesItAndWhatStopsWorking()
    {
        var words = ConfirmCopy.RemoveIntegration("ConnectWise Manage");

        Assert.Equal("Remove ConnectWise Manage?", words.Heading);
        Assert.Equal("Remove ConnectWise Manage", words.Action);
        Assert.Contains("ConnectWise Manage", words.Consequence, StringComparison.Ordinal);
        Assert.True(words.CancelIsDefault);
    }

    [Fact]
    public void ARefusalIsANoticeWithOneButton()
    {
        // Something the service would not do is said, with one way out, never an OK.
        var words = ConfirmCopy.NotDiscarded("The capture service did not answer.");

        Assert.Null(words.Action);
        Assert.Equal("Close", words.Cancel);
        Assert.Equal("The session was not discarded", words.Heading);
        Assert.StartsWith("The capture service did not answer.", words.Consequence, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDialogMadeOfDestructiveWordsLetsCancelBeTheDefault()
    {
        // Enter on a dialog that destroys something keeps it; only typing the phrase, or a click, discards.
        Assert.All(
            new[] { ConfirmCopy.DiscardSession, ConfirmCopy.DiscardSessions(2), ConfirmCopy.DeleteEverything, ConfirmCopy.QuitWhileCapturing },
            words => Assert.True(words.CancelIsDefault));
    }
}
