using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;
using ScreenTail.Shared.Schema;

namespace ScreenTail.Tests.Capture;

/// <summary>
/// INV-5 where it meets the capture path, after an adversarial review found the scope decision carrying
/// nothing that identified the window it was about.
///
/// A capture taken moments later photographed whatever had the foreground by then, so a decision about
/// ScreenConnect could authorise a screenshot of Outlook. This covers the half that is arithmetic — that
/// the decision names its window. Whether the capturer honours it is in
/// <c>ScreenTail.Tests.Windows.Capture.ScreenshotTests</c>, against the real screen, because a fake
/// capturer here would only be testing the fake.
/// </summary>
public sealed class ScopeEnforcementTests
{
    private static readonly RemoteToolRegistry Registry = RemoteToolRegistry.Load(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));

    [Fact]
    public void AnInScopeDecisionSaysWhichWindowItIsAbout()
    {
        // The whole fix. A decision that does not name its window cannot be checked against the window a
        // capture is about to read.
        var decision = new ScopePolicy(Registry).Decide(Window("mstsc", handle: 4242));

        Assert.True(decision.MayCaptureFrames);
        Assert.Equal(4242, decision.Window);
    }

    [Fact]
    public void TwoWindowsOfTheSameToolAreTwoDecisions()
    {
        // A technician with two remote sessions open. Both are in scope, and a frame authorised for one
        // must not be taken of the other — so the handle has to distinguish them, not the tool id.
        var policy = new ScopePolicy(Registry);

        var first = policy.Decide(Window("mstsc", handle: 1));
        var second = policy.Decide(Window("mstsc", handle: 2));

        Assert.Equal(first.ToolId, second.ToolId);
        Assert.NotEqual(first.Window, second.Window);
    }

    [Theory]
    [InlineData("outlook")]
    [InlineData("chrome")]
    [InlineData("notepad")]
    public void AnOutOfScopeWindowAuthorisesNoCapture(string process)
    {
        // Not "explorer": it is an admin tool in the shipped registry and legitimately in scope beside a
        // session. Assuming otherwise is how a test ends up asserting the opposite of the design.
        Assert.False(new ScopePolicy(Registry).Decide(Window(process, handle: 11)).MayCaptureFrames);
    }

    [Fact]
    public void OutOfScopeKeystrokeCountsAreNotWritten()
    {
        // INV-6: "excluded app, elevated window, out-of-scope → no frames, no typing events written."
        // Before this, the capture loop gated only on the session state and wrote keystroke counts for
        // whatever the technician alt-tabbed to — their own password manager during the auto-stop grace
        // period, for instance.
        var outOfScope = new ScopePolicy(Registry).Decide(Window("outlook", handle: 11));

        Assert.False(outOfScope.MayRecord(new TypingBurstEvent { TsMs = 1, CharCount = 12 }));
        Assert.False(outOfScope.MayRecord(new ShortcutEvent { TsMs = 1 }));
        Assert.False(outOfScope.MayRecord(new EnterEvent { TsMs = 1 }));
    }

    [Fact]
    public void OutOfScopeClicksAreStillWritten()
    {
        // The deliberate exception, and ST-023's own criterion: "switching to Outlook logs clicks but
        // captures no frames". Dropping these too would turn "clicks logged, no frames" in Review into a
        // silent gap, and would fail ST-023 while satisfying nothing INV-6 asks for.
        var outOfScope = new ScopePolicy(Registry).Decide(Window("outlook", handle: 11));

        Assert.True(outOfScope.MayRecord(new ClickEvent { TsMs = 1, X = 4, Y = 5, Button = MouseButton.Left }));
    }

    [Fact]
    public void InScopeEverythingIsWritten()
    {
        var inScope = new ScopePolicy(Registry).Decide(Window("mstsc", handle: 11));

        Assert.True(inScope.MayRecord(new TypingBurstEvent { TsMs = 1, CharCount = 12 }));
        Assert.True(inScope.MayRecord(new ClickEvent { TsMs = 1, X = 4, Y = 5, Button = MouseButton.Left }));
        Assert.True(inScope.MayRecord(new ShortcutEvent { TsMs = 1 }));
    }

    [Fact]
    public void AnExcludedWindowWritesNoKeystrokesEither()
    {
        // INV-6 names excluded apps in the same breath as out-of-scope, and ST-043's list is what will
        // populate that branch. The rule must not be written as "out of scope only".
        var excluded = new ScopeDecision(CaptureScope.Excluded, null, null, "Not capturing — 1Password is on your excluded list.");

        Assert.False(excluded.MayCaptureFrames);
        Assert.False(excluded.MayRecord(new TypingBurstEvent { TsMs = 1, CharCount = 9 }));
    }

    private static ForegroundWindowInfo Window(string process, nint handle) =>
        new(handle, 42, process, "a window", "Window", null, false, DateTimeOffset.UnixEpoch);
}
