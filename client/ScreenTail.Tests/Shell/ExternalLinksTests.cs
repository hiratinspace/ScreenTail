using ScreenTail.Core.Shell;

namespace ScreenTail.Tests.Shell;

/// <summary>
/// What the UI hands to the Windows shell (T-129): "Open in ConnectWise" and the onboarding "Fix". The
/// shell launches files, programs and any registered protocol, so the rule lives here, tested on any
/// machine, and the two buttons only call it.
/// </summary>
public sealed class ExternalLinksTests
{
    [Theory]
    [InlineData("https://na.myconnectwise.net/v4_6_release/services/system_io/Service/fv_sr100_request.rails?service_recid=48213")]
    [InlineData("HTTPS://acme.huducloud.com/kba/1")]
    [InlineData("https://cw.example:8443/1")]
    public void AnHttpsLinkWithAHostOpens(string link) =>
        Assert.True(ExternalLinks.IsWebLink(new Uri(link)));

    [Theory]
    [InlineData("http://cw.example/1")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:privacy-microphone")]
    [InlineData("ftp://cw.example/1")]
    [InlineData("https://na.myconnectwise.net@elsewhere.example/1")]
    [InlineData("https://user:password@cw.example/1")]
    [InlineData("\\\\server\\share\\run.exe")]
    public void OpenLinkIgnoresAnythingElse(string link) =>
        Assert.False(ExternalLinks.IsWebLink(new Uri(link, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void ARelativeOrMissingLinkDoesNotOpen()
    {
        Assert.False(ExternalLinks.IsWebLink(null));
        Assert.False(ExternalLinks.IsWebLink(new Uri("/v4_6_release/1", UriKind.Relative)));
    }

    [Theory]
    [InlineData("ms-settings:privacy-microphone", "ms-settings:privacy-microphone")]
    [InlineData("MS-SETTINGS:privacy-webcam", "MS-SETTINGS:privacy-webcam")]
    [InlineData("https://learn.example/fix", "https://learn.example/fix")]
    [InlineData("https://learn.example/a b", "https://learn.example/a%20b")]
    public void AFixOpensASettingsPageOrAWebLink(string link, string expected)
    {
        Assert.True(ExternalLinks.TryFixLink(link, out var target));
        Assert.Equal(expected, target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ms-settings:")]
    [InlineData("ms-settings:privacy microphone")]
    [InlineData("ms-settings:privacy-microphone&calc")]
    [InlineData("ms-settings:privacy\u2010microphone")]
    [InlineData("ms-settings:privacy-microphone-and-a-name-far-longer-than-any-windows-settings-page")]
    [InlineData(" ms-settings:privacy-microphone")]
    [InlineData("http://learn.example/fix")]
    [InlineData("https://learn.example@elsewhere.example/fix")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("calc.exe")]
    [InlineData("Turn on microphone access in Windows Settings.")]
    public void OpenFixIgnoresAnythingElse(string? link)
    {
        Assert.False(ExternalLinks.TryFixLink(link, out var target));
        Assert.Null(target);
    }
}
