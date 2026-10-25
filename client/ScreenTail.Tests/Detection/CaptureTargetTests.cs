using ScreenTail.Core.Detection;

namespace ScreenTail.Tests.Detection;

/// <summary>
/// The rules the Windows capturer and watcher apply to what they read about a window: whether something lies
/// over it, whether its process is elevated, and what its process is called (ST-022, ST-025). The Windows
/// suite runs them against real windows; these pin the rules themselves.
/// </summary>
public sealed class CaptureTargetTests
{
    private static readonly ScreenRect Target = new(100, 100, 900, 700);

    private static WindowAbove Toast(ScreenRect? bounds = null) => new(
        bounds ?? new ScreenRect(700, 600, 1000, 760),
        Visible: true,
        Cloaked: false,
        Topmost: true,
        SameProcess: false,
        ExcludedFromCapture: false,
        Taskbar: false);

    // ---- a window over the window in front -----------------------------------------------------------

    [Fact]
    public void AnotherAppsTopmostWindowOverTheWindowInFrontCoversIt() =>
        // A toast in the corner of the window is in a frame copied off the screen (2026-10-12 review, S-4).
        Assert.True(WindowCover.Covers(Target, Toast()));

    [Fact]
    public void AnOrdinaryWindowOfAnotherAppOverTheWindowInFrontCoversItToo() =>
        // Only topmost windows used to count. A window that is not topmost can still lie above the one in
        // front: shown without taking the foreground, or put there by its own app, and its pixels are in
        // a frame copied off the screen all the same (c8 review). Whatever is above and foreign is cover.
        Assert.True(WindowCover.Covers(Target, Toast() with { Topmost = false }));

    [Theory]
    [InlineData(900, 100, 1200, 300)]
    [InlineData(100, 700, 900, 900)]
    [InlineData(1000, 1000, 1200, 1200)]
    public void ATopmostWindowBesideTheWindowInFrontDoesNotCoverIt(int left, int top, int right, int bottom) =>
        // Touching an edge shares no pixel; beside it shares none either.
        Assert.False(WindowCover.Covers(Target, Toast(new ScreenRect(left, top, right, bottom))));

    [Fact]
    public void WhatIsNotOnScreenOrNotInTheFrameCoversNothing()
    {
        Assert.False(WindowCover.Covers(Target, Toast() with { Visible = false }));
        Assert.False(WindowCover.Covers(Target, Toast() with { Cloaked = true }));

        // The HUD is topmost over every window, and sets itself out of capture: it is not in the frame.
        Assert.False(WindowCover.Covers(Target, Toast() with { ExcludedFromCapture = true }));
        Assert.False(WindowCover.Covers(Target, Toast(new ScreenRect(500, 500, 500, 800))));
    }

    [Fact]
    public void TheAppInFrontsOwnMenuAndTheTaskbarDoNotCoverIt()
    {
        // A click on a menu is a picture of the menu, and the taskbar is the shell's strip along an edge.
        Assert.False(WindowCover.Covers(Target, Toast() with { SameProcess = true }));
        Assert.False(WindowCover.Covers(Target, Toast() with { Taskbar = true }));
    }

    [Fact]
    public void AWalkUpTheZOrderThatWasCutShortSaysCovered()
    {
        // What was not looked at may be on top, so an unfinished walk is not evidence of a clear window.
        Assert.True(WindowCover.AnyCovers(Target, [], walkComplete: false));
        Assert.False(WindowCover.AnyCovers(Target, [], walkComplete: true));
        Assert.False(WindowCover.AnyCovers(Target, [Toast() with { SameProcess = true }], walkComplete: true));
        Assert.True(WindowCover.AnyCovers(Target, [Toast() with { SameProcess = true }, Toast()], walkComplete: true));
    }

    // ---- elevation (INV-6) ---------------------------------------------------------------------------

    [Theory]
    [InlineData(ProcessElevation.High)]
    [InlineData(ProcessElevation.System)]
    public void AnElevatedProcessIsElevatedThoughItCouldBeOpened(int level) =>
        // Elevation used to mean "could not be opened", and a same-user elevated process opens without
        // complaint (2026-10-12 review, S-8). Its integrity level says what it is.
        Assert.True(ProcessElevation.IsElevated(level, ProcessElevation.Medium, isOwnProcess: false));

    [Fact]
    public void AnOrdinaryProcessIsNotElevated()
    {
        Assert.False(ProcessElevation.IsElevated(ProcessElevation.Medium, ProcessElevation.Medium, isOwnProcess: false));

        // An app container's low level is below this process, not above it.
        Assert.False(ProcessElevation.IsElevated(0x1000, ProcessElevation.Medium, isOwnProcess: false));
    }

    [Fact]
    public void AProcessWhoseLevelCannotBeReadIsTreatedAsElevated() =>
        // Blind is the closed answer: a window nothing is known about is not one that is photographed.
        Assert.True(ProcessElevation.IsElevated(null, ProcessElevation.Medium, isOwnProcess: false));

    [Fact]
    public void WithUacOffAWindowAtTheServicesOwnHighLevelIsNotElevated()
    {
        // With UAC off every process the technician starts runs at High, the service included. Counting
        // High as elevated whatever the service ran at made every window "elevated" there, and nothing was
        // captured at all (c8 review). Elevated is above the service's own level, which at Medium is High.
        Assert.False(ProcessElevation.IsElevated(ProcessElevation.High, ProcessElevation.High, isOwnProcess: false));
        Assert.False(ProcessElevation.IsElevated(ProcessElevation.Medium, ProcessElevation.High, isOwnProcess: false));

        // Above it is still elevated: a SYSTEM process is not the technician's to photograph.
        Assert.True(ProcessElevation.IsElevated(ProcessElevation.System, ProcessElevation.High, isOwnProcess: false));
    }

    [Fact]
    public void WhenTheServicesOwnLevelCannotBeReadEveryWindowIsTreatedAsElevated()
    {
        // Nothing can be said to be above or below a level that is not known, so the closed answer is the
        // one INV-6 asks for: no frames rather than frames of a window that might be elevated.
        Assert.True(ProcessElevation.IsElevated(ProcessElevation.Medium, null, isOwnProcess: false));
        Assert.True(ProcessElevation.IsElevated(0x1000, null, isOwnProcess: false));
    }

    [Fact]
    public void AnythingAboveThisProcessIsElevated() =>
        Assert.True(ProcessElevation.IsElevated(ProcessElevation.Medium, 0x1000, isOwnProcess: false));

    [Fact]
    public void ThisProcessesOwnWindowsAreNotElevatedToItself()
    {
        Assert.False(ProcessElevation.IsElevated(ProcessElevation.High, ProcessElevation.High, isOwnProcess: true));
        Assert.False(ProcessElevation.IsElevated(null, null, isOwnProcess: true));
    }

    // ---- what a process is called (S-10) -------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Program Files\WSL\msrdc.exe", "wslg")]
    [InlineData(@"c:\program files\wsl\MSRDC.EXE", "wslg")]
    [InlineData(@"C:\Program Files\WindowsApps\MicrosoftCorporationII.WindowsSubsystemForLinux_2.0.9.0_x64__8wekyb3d8bbwe\msrdc.exe", "wslg")]
    [InlineData(@"C:\Windows\System32\lxss\wslg\msrdc.exe", "wslg")]
    public void WslgsCopyOfTheRemoteDesktopClientIsReportedAsWslg(string imagePath, string expected) =>
        // WSLg shows Linux GUI apps through an msrdc.exe installed with WSL (2026-10-12 review, S-10).
        Assert.Equal(expected, ProcessImage.NameFor(imagePath));

    [Theory]
    [InlineData(@"C:\Program Files\Remote Desktop\msrdc.exe", "msrdc")]
    [InlineData(@"C:\Users\tech\AppData\Local\Apps\Remote Desktop\msrdc.exe", "msrdc")]
    [InlineData(@"C:\Program Files\WindowsApps\MicrosoftCorporationII.Windows365_2.0.1.0_x64__8wekyb3d8bbwe\msrdc.exe", "msrdc")]
    [InlineData(@"C:\Windows\System32\mstsc.exe", "mstsc")]
    [InlineData(@"C:\Program Files\WSL\wsl.exe", "wsl")]
    [InlineData(@"C:\Program Files\ScreenConnect Client (abc)\ScreenConnect.WindowsClient.exe", "ScreenConnect.WindowsClient")]
    [InlineData("notepad", "notepad")]
    public void EveryOtherImageIsReportedUnderItsFileName(string imagePath, string expected) =>
        // Azure Virtual Desktop and Windows 365 keep the msrdc name, and so stay remote desktop sessions.
        Assert.Equal(expected, ProcessImage.NameFor(imagePath));
}
