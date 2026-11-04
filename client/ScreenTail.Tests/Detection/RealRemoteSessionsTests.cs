using ScreenTail.Core.Detection;
using ScreenTail.Core.Detection.Registry;

namespace ScreenTail.Tests.Detection;

/// <summary>
/// The rule the Windows stop-time test uses to refuse to run while a real remote-support session is on the
/// desktop (ST-112): visible windows of a remote tool or a browser on one, never a background agent, never
/// the test's own synthetic window.
/// </summary>
public sealed class RealRemoteSessionsTests
{
    private const int Own = 4242;

    private static readonly RemoteToolRegistry Shipped = RemoteToolRegistry.Load(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Registry", "remote-tools.json")));

    private static ForegroundWindowInfo Window(int processId, string? process, string title = "", string @class = "Window") =>
        new(processId * 10, processId, process, title, @class, BrowserTabTitle: null, IsElevated: false, DateTimeOffset.UnixEpoch);

    [Fact]
    public void TheTestsOwnRemoteToolShapedWindowDoesNotCount() =>
        Assert.Equal(0, RealRemoteSessions.Count(Shipped, [Window(Own, "dotnet", "Stop time test session", "ScreenConnect.ScreenTailTestWindow")], Own));

    [Theory]
    [InlineData("ScreenConnect.WindowsClient", "Window")]
    [InlineData("mstsc", "Window")]
    [InlineData("AnyDesk", "Window")]
    [InlineData("SomethingElse", "ScreenConnect.Something")]
    [InlineData("SomethingElse", "TscShellContainerClass")]
    public void AVisibleWindowOfARemoteToolCounts(string process, string @class) =>
        Assert.Equal(1, RealRemoteSessions.Count(Shipped, [Window(7, process, "a session", @class)], Own));

    [Theory]
    [InlineData("chrome", "ConnectWise Control - Host - Google Chrome")]
    [InlineData("msedge", "Azure Virtual Desktop and 2 more pages - Microsoft Edge")]
    public void ABrowserShowingARemoteToolByItsTitleCounts(string process, string title) =>
        Assert.Equal(1, RealRemoteSessions.Count(Shipped, [Window(8, process, title)], Own));

    [Theory]
    [InlineData("chrome", "Inbox - Google Chrome")]
    [InlineData("notepad", "ConnectWise Control notes")]
    [InlineData("explorer", "")]
    public void OrdinaryWindowsDoNotCount(string process, string title) =>
        Assert.Equal(0, RealRemoteSessions.Count(Shipped, [Window(9, process, title)], Own));

    [Fact]
    public void EachMatchingWindowIsCountedAndNothingElse() =>
        Assert.Equal(2, RealRemoteSessions.Count(
            Shipped,
            [
                Window(Own, "dotnet", string.Empty, "ScreenConnect.ScreenTailTestWindow"),
                Window(1, "mstsc"),
                Window(2, "chrome", "ScreenConnect - Google Chrome"),
                Window(3, "chrome", "News - Google Chrome"),
                ForegroundWindowInfo.None(DateTimeOffset.UnixEpoch),
            ],
            Own));
}
