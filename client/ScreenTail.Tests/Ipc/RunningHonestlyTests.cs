using ScreenTail.Core.Ipc;

namespace ScreenTail.Tests.Ipc;

/// <summary>
/// A signed process that is running somebody else's code (ST-012; 2026-09-19 review).
///
/// The peer check verifies the file a process was started from, which is worth having and is not enough
/// on its own: .NET will load another assembly into a perfectly genuine, perfectly signed process if the
/// environment asks it to. <c>DOTNET_STARTUP_HOOKS</c> runs an initialiser before <c>Main</c>, and the
/// profiler pair loads a native DLL with full access to the runtime.
///
/// So an attacker needs no forged binary at all: launch our real, signed UI with a hook set, and it
/// passes the signature check for the same reason the genuine one does — while somebody else's code is
/// already running inside it, holding the session token. The defence is that the code being hijacked is
/// ours, so it can refuse.
/// </summary>
public sealed class RunningHonestlyTests
{
    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER_PATH")]
    public void ASignedBuildWillNotStartAlongsideSomebodyElsesCode(string variable)
    {
        var refusal = RunningHonestly.WhyNotToStart(development: false, Set(variable, "C:\\somewhere\\hook.dll"));

        Assert.NotNull(refusal);
        Assert.Contains(variable, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalNamesTheVariableAndNotItsValue()
    {
        // A path somebody chose has no business in a message a customer may be reading over a
        // technician's shoulder, and the name is the whole of what anyone needs to act (INV-10).
        var refusal = RunningHonestly.WhyNotToStart(development: false, Set("DOTNET_STARTUP_HOOKS", "C:\\evil\\payload.dll"));

        Assert.NotNull(refusal);
        Assert.DoesNotContain("payload.dll", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalSaysWhatToDoAboutIt()
    {
        // Spec §4: what happened, then what to do. "Refused to start" with no next step is a support call.
        var refusal = RunningHonestly.WhyNotToStart(development: false, Set("DOTNET_STARTUP_HOOKS", "hook.dll"));

        Assert.Contains("Unset it", refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void ADevelopmentBuildStillStarts()
    {
        // Nobody signed it, and this is how a profiler is attached. A rule that makes debugging
        // impossible is a rule somebody deletes, and then it protects nothing at all.
        Assert.Null(RunningHonestly.WhyNotToStart(development: true, Set("DOTNET_STARTUP_HOOKS", "hook.dll")));
    }

    [Fact]
    public void ADevelopmentBuildStillSaysSo()
    {
        // Not silent, though: whoever is debugging should know their profiler is loaded, and whoever is
        // reading a bug report should be able to tell that this process was not running alone.
        Assert.Equal(
            ["DOTNET_STARTUP_HOOKS"],
            RunningHonestly.ForeignCodeRequested(Set("DOTNET_STARTUP_HOOKS", "hook.dll")));
    }

    [Fact]
    public void AnOrdinaryProcessStartsWithNothingToSay()
    {
        Assert.Null(RunningHonestly.WhyNotToStart(development: false, _ => null));
        Assert.Empty(RunningHonestly.ForeignCodeRequested(_ => null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AVariableSetToNothingIsNotSet(string value) =>
        Assert.Null(RunningHonestly.WhyNotToStart(development: false, Set("DOTNET_STARTUP_HOOKS", value)));

    [Fact]
    public void EveryOneThatIsSetIsNamed()
    {
        var refusal = RunningHonestly.WhyNotToStart(
            development: false,
            name => name is "DOTNET_STARTUP_HOOKS" or "CORECLR_PROFILER_PATH" ? "something" : null);

        Assert.Contains("DOTNET_STARTUP_HOOKS", refusal!, StringComparison.Ordinal);
        Assert.Contains("CORECLR_PROFILER_PATH", refusal, StringComparison.Ordinal);
    }

    private static Func<string, string?> Set(string variable, string value) =>
        name => string.Equals(name, variable, StringComparison.Ordinal) ? value : null;
}
