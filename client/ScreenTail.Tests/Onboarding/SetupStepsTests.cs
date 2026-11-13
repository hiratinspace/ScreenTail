using ScreenTail.Core.Onboarding;

namespace ScreenTail.Tests.Onboarding;

/// <summary>
/// The wizard's step list on the frame (Spec §5 S8, as built v0.6.7): every step by name in the spec's
/// order, the ones behind done, the one in hand current, and each row in words for a screen reader.
/// </summary>
public sealed class SetupStepsTests
{
    [Fact]
    public void TheListIsTheSpecsSevenStepsInOrderInPlainWords()
    {
        var rows = SetupSteps.For(OnboardingStep.Welcome);

        Assert.Equal(["Welcome", "Activate", "Permissions", "Remote tools", "Connect", "Try it", "Done"], rows.Select(r => r.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], rows.Select(r => r.Number));
        Assert.All(rows, r => Assert.Equal(7, r.Count));
    }

    [Theory]
    [InlineData(OnboardingStep.Welcome, 0)]
    [InlineData(OnboardingStep.Permissions, 2)]
    [InlineData(OnboardingStep.Done, 6)]
    public void StepsBehindAreDoneTheOneInHandIsCurrentAndTheRestAreAhead(OnboardingStep current, int behind)
    {
        var rows = SetupSteps.For(current);

        Assert.All(rows.Take(behind), r => Assert.Equal(SetupStepState.Done, r.State));
        Assert.Equal(SetupStepState.Current, rows[behind].State);
        Assert.All(rows.Skip(behind + 1), r => Assert.Equal(SetupStepState.Ahead, r.State));

        // Exactly one row is the technician's, whatever the step.
        _ = Assert.Single(rows, r => r.IsCurrent);
        Assert.Equal(behind, rows.Count(r => r.IsDone));
    }

    [Fact]
    public void OnlyConnectIsMarkedOptional()
    {
        // Spec §5 S8 step 5: "skippable: You can publish later". Activate can be skipped too, for a device
        // enrolled from the environment, but the list does not offer that as a choice.
        var rows = SetupSteps.For(OnboardingStep.Welcome);

        var optional = Assert.Single(rows, r => r.Optional);
        Assert.Equal("Connect", optional.Name);
        Assert.Equal("Optional", optional.Tag);
        Assert.All(rows.Where(r => !r.Optional), r => Assert.Empty(r.Tag));
    }

    [Fact]
    public void EachRowSaysItsNumberNameAndStateInWords()
    {
        var rows = SetupSteps.For(OnboardingStep.Permissions);

        Assert.Equal("Step 1 of 7: Welcome, done", rows[0].Spoken);
        Assert.Equal("Step 3 of 7: Permissions, this step", rows[2].Spoken);
        Assert.Equal("Step 5 of 7: Connect, optional, still to do", rows[4].Spoken);
        Assert.Equal("Step 7 of 7: Done, still to do", rows[6].Spoken);

        // Label in name (WCAG 2.5.3): what the row shows is in what it is called.
        Assert.All(rows, r => Assert.Contains(r.Name, r.Spoken, StringComparison.Ordinal));
    }

    [Fact]
    public void TheListFollowsTheWizard()
    {
        var wizard = OnboardingWizardTests.WizardFor([]);
        Assert.Equal("Welcome", SetupSteps.For(wizard.Step).Single(r => r.IsCurrent).Name);

        wizard.Next();
        Assert.Equal("Activate", SetupSteps.For(wizard.Step).Single(r => r.IsCurrent).Name);
        Assert.Equal(wizard.StepNumber, SetupSteps.For(wizard.Step).Single(r => r.IsCurrent).Number);
        Assert.Equal(wizard.StepCount, SetupSteps.For(wizard.Step).Count);
    }
}
