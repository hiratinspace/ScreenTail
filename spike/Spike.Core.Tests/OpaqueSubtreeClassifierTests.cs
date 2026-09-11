namespace ScreenTail.Spike.Core.Tests;

public class OpaqueSubtreeClassifierTests
{
    [Fact]
    public void RdpCanvasClass_IsOpaque()
    {
        var verdict = OpaqueSubtreeClassifier.Classify(
            new UiaFocusSnapshot("TscShellContainerClass", "IHWindowClass", "Pane", 0, 0.97));

        Assert.Equal(SubtreeKind.Opaque, verdict.Kind);
    }

    [Fact]
    public void LargeChildlessPane_IsOpaque()
    {
        var verdict = OpaqueSubtreeClassifier.Classify(
            new UiaFocusSnapshot("WindowsForms10.Window", "WindowsForms10.Window.8.app", "Pane", 0, 0.9));

        Assert.Equal(SubtreeKind.Opaque, verdict.Kind);
    }

    [Fact]
    public void LargeChildlessDocument_IsAccessible()
    {
        // Notepad's editor is childless and fills the window but exposes TextPattern.
        var verdict = OpaqueSubtreeClassifier.Classify(
            new UiaFocusSnapshot("Notepad", "RichEditD2DPT", "Document", 0, 0.9));

        Assert.Equal(SubtreeKind.Accessible, verdict.Kind);
    }

    [Fact]
    public void PaneWithChildren_IsAccessible()
    {
        var verdict = OpaqueSubtreeClassifier.Classify(
            new UiaFocusSnapshot("#32770", "SysListView32", "Pane", 12, 0.8));

        Assert.Equal(SubtreeKind.Accessible, verdict.Kind);
    }

    [Fact]
    public void SmallChildlessPane_IsAccessible()
    {
        var verdict = OpaqueSubtreeClassifier.Classify(
            new UiaFocusSnapshot("#32770", "Static", "Pane", 0, 0.1));

        Assert.Equal(SubtreeKind.Accessible, verdict.Kind);
    }
}
