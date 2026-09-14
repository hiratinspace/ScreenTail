using ScreenTail.Core.Review;

namespace ScreenTail.Tests.Review;

/// <summary>ST-074: the gate in front of Discard (Spec §3, §5 S3, §5 S4).</summary>
public sealed class TypedConfirmationTests
{
    [Theory]
    [InlineData("discard")]
    [InlineData("Discard")]
    [InlineData("DISCAR")]
    [InlineData("DISCARD IT")]
    [InlineData("")]
    [InlineData(null)]
    public void OnlyTheWordOnScreenGetsThrough(string? typed)
    {
        // Accepting "discard" would let the muscle memory that types everything lowercase sail straight
        // through, and that memory is what the gate exists to interrupt. The word is on screen, so typing
        // it exactly asks nothing of anyone.
        Assert.False(TypedConfirmation.ForDiscard().Accepts(typed));
    }

    [Fact]
    public void TheWhitespaceAPasteBringsWithItIsForgiven()
    {
        Assert.True(TypedConfirmation.ForDiscard().Accepts("  DISCARD\n"));
    }

    [Fact]
    public void TheDialogShowsWhatToType()
    {
        Assert.Equal("Type DISCARD to confirm", TypedConfirmation.ForDiscard().Prompt);
    }

    [Fact]
    public void BulkDiscardAsksForTheNumberSelected()
    {
        // Spec §5 S4 (v0.4.1 Q6): the same rule, a different token, and the token is a fact the technician
        // has to look at the screen to know.
        var three = TypedConfirmation.ForCount(3);

        Assert.Equal("Type 3 to confirm", three.Prompt);
        Assert.True(three.Accepts("3"));
        Assert.False(three.Accepts("2"));
    }

    [Fact]
    public void AConfirmationWithNothingToTypeIsRefusedOutright()
    {
        // It would render as a dialog whose empty box is already correct, which is worse than no dialog:
        // it looks like a gate.
        Assert.Throws<ArgumentException>(() => new TypedConfirmation("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => TypedConfirmation.ForCount(0));
    }
}
