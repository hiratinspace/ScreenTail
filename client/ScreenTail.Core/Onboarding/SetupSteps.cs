using System.Globalization;

namespace ScreenTail.Core.Onboarding;

/// <summary>Where a step stands against the one the technician is on.</summary>
public enum SetupStepState
{
    Done,
    Current,
    Ahead,
}

/// <summary>
/// One row of the wizard's step list (Spec §5 S8, as built v0.6.7): the step's number, its short name and
/// where it stands. The list is progress to read, not navigation: the wizard's own rules decide the order.
/// </summary>
/// <param name="Optional">Connect, which the spec lets the technician skip ("You can publish later").</param>
public sealed record SetupStepRow(int Number, int Count, string Name, SetupStepState State, bool Optional)
{
    public bool IsDone => State == SetupStepState.Done;

    public bool IsCurrent => State == SetupStepState.Current;

    /// <summary>The word under an optional step's name, or nothing.</summary>
    public string Tag => Optional ? "Optional" : string.Empty;

    /// <summary>
    /// The row in words, for a screen reader: the check, the ring and the weight say nothing to one
    /// (Spec §7: no state is colour or shape alone).
    /// </summary>
    public string Spoken
    {
        get
        {
            var state = State switch
            {
                SetupStepState.Done => "done",
                SetupStepState.Current => "this step",
                _ => "still to do",
            };
            var optional = Optional ? ", optional" : string.Empty;
            return string.Create(CultureInfo.InvariantCulture, $"Step {Number} of {Count}: {Name}{optional}, {state}");
        }
    }
}

/// <summary>The wizard's seven steps as the step list shows them, in the spec's order and plain words.</summary>
public static class SetupSteps
{
    /// <summary>The step's short name in the list. The step's own title, beside it, says more.</summary>
    public static string Name(OnboardingStep step) => step switch
    {
        OnboardingStep.Welcome => "Welcome",
        OnboardingStep.Activate => "Activate",
        OnboardingStep.Permissions => "Permissions",
        OnboardingStep.Tools => "Remote tools",
        OnboardingStep.Connect => "Connect",
        OnboardingStep.TryIt => "Try it",
        _ => "Done",
    };

    /// <summary>Every step, each marked against <paramref name="current"/>: before it done, after it ahead.</summary>
    public static IReadOnlyList<SetupStepRow> For(OnboardingStep current)
    {
        var steps = Enum.GetValues<OnboardingStep>();
        return [.. steps.Select(step => new SetupStepRow(
            (int)step + 1,
            steps.Length,
            Name(step),
            step < current ? SetupStepState.Done : step == current ? SetupStepState.Current : SetupStepState.Ahead,
            step == OnboardingStep.Connect))];
    }
}
