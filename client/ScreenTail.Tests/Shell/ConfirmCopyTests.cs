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

    private static Dictionary<string, ConfirmWords> Every() => new(StringComparer.Ordinal)
    {
        ["discard"] = ConfirmCopy.DiscardSession,
        ["bulk-1"] = ConfirmCopy.DiscardSessions(1),
        ["bulk-3"] = ConfirmCopy.DiscardSessions(3),
        ["erase"] = ConfirmCopy.DeleteEverything,
        ["mark"] = ConfirmCopy.MarkAsPublished("Tue 10 Nov 14:02, 9:00, ScreenConnect"),
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
            Assert.DoesNotContain(button, new[] { "OK", "Ok", "Yes", "No", "Confirm" });
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
        Assert.Equal(ConfirmTone.Primary, ConfirmCopy.MarkAsPublished("Tue 10 Nov 14:02, 9:00, ScreenConnect").Tone);
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
        // Spec §5 S4 (v0.5.3): the question names the session as its row does, then says what follows.
        var words = ConfirmCopy.MarkAsPublished("Tue 10 Nov 14:02, 9:00, ScreenConnect");

        Assert.Equal("Mark the session started Tue 10 Nov 14:02, 9:00, ScreenConnect as published?", words.Heading);
        Assert.Equal(MarkAsPublished.Question, words.Consequence);
        Assert.Equal(MarkAsPublished.Action, words.Action);
        Assert.True(words.CancelIsDefault, "Enter must not vouch for a ticket by reflex.");
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
        Assert.Equal("The capture service did not answer.", words.Consequence);
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
