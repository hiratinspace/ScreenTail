using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// The diagnostics panel's headline as a state with a glyph and a colour, never colour alone (Spec §1.1,
/// §7), its Local-only badge (Spec §5 S1), and what it says once Copy diagnostics has run (v0.6.11).
/// </summary>
public sealed class DiagnosticsPanelStateTests
{
    private static Diagnostics With(string scope, string? suppression = null, bool localOnly = false) => new(
        Scope: scope,
        Microphone: "Headset Microphone",
        Suppression: suppression,
        RedactionBacklog: 0,
        FramesDropped: 0,
        KeystrokesDropped: 0,
        EgressBlocked: 0,
        NameHookRefusals: 0,
        LocalOnly: localOnly,
        PolicyVersion: "policy-1",
        CpuPercent: 1,
        WorkingSetBytes: 1,
        ClientVersion: "0.1.0");

    [Theory]
    [InlineData("Capturing: ScreenConnect", null, DiagnosticsState.Capturing)]
    [InlineData("Not capturing: Outlook is on your excluded list.", null, DiagnosticsState.NotCapturing)]
    [InlineData("Not capturing: no remote session in front", null, DiagnosticsState.NotCapturing)]
    [InlineData("Capturing: ScreenConnect", "sensitive field", DiagnosticsState.Paused)]
    public void TheHeadlineHasAState(string scope, string? suppression, DiagnosticsState expected)
    {
        Assert.Equal(expected, DiagnosticsPanel.From(With(scope, suppression)).State);
    }

    [Fact]
    public void AnUnansweredPanelIsUnknownNotIdle()
    {
        Assert.Equal(DiagnosticsState.Unknown, DiagnosticsPanel.Unanswered.State);
        Assert.Null(DiagnosticsPanel.Unanswered.LocalOnly);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LocalOnlyIsWhatTheServiceSaid(bool localOnly)
    {
        Assert.Equal(localOnly, DiagnosticsPanel.From(With("Capturing: RDP", localOnly: localOnly)).LocalOnly);
    }

    [Fact]
    public void CopyingSaysWhereItWentAndThatItHoldsNoContent()
    {
        Assert.Contains("clipboard", DiagnosticsPanel.Copied, StringComparison.Ordinal);
        Assert.Contains("no screen text", DiagnosticsPanel.Copied, StringComparison.Ordinal);
        Assert.DoesNotContain('—', DiagnosticsPanel.Copied);
    }
}
