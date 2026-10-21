using System.Diagnostics.CodeAnalysis;

namespace ScreenTail.Core.Shell;

/// <summary>
/// Which links the UI may hand to the Windows shell (T-129). "Open in ConnectWise" opens what a provider
/// sent back, and a capability check's "Fix" opens what the service reported; the shell will launch a
/// file, a program or any registered protocol it is given, so both go through here first, where the rule
/// can be tested on any machine (ADR-0002).
/// </summary>
public static class ExternalLinks
{
    private const string SettingsScheme = "ms-settings:";

    /// <summary>Windows Settings page names are short; nothing longer is one.</summary>
    private const int MaxSettingsPage = 64;

    /// <summary>
    /// An absolute https link with a host and no user name or password in it. The last rule is there
    /// because <c>https://connectwise.com@elsewhere.example</c> reads as one site and opens another.
    /// </summary>
    public static bool IsWebLink([NotNullWhen(true)] Uri? link) =>
        link is { IsAbsoluteUri: true }
        && link.Scheme == Uri.UriSchemeHttps
        && link.Host.Length > 0
        && link.UserInfo.Length == 0;

    /// <summary>
    /// A fix the onboarding checklist may open: a Windows Settings page by name
    /// (<c>ms-settings:privacy-microphone</c>) or a web link as <see cref="IsWebLink"/> has it.
    /// <paramref name="target"/> is what to open: the page as given, or the web link in its canonical form.
    /// </summary>
    public static bool TryFixLink(string? link, [NotNullWhen(true)] out string? target)
    {
        target = null;
        if (string.IsNullOrEmpty(link))
        {
            return false;
        }

        if (link.StartsWith(SettingsScheme, StringComparison.OrdinalIgnoreCase))
        {
            var page = link.AsSpan(SettingsScheme.Length);
            if (page.Length is 0 or > MaxSettingsPage)
            {
                return false;
            }

            foreach (var c in page)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }

            target = link;
            return true;
        }

        if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && IsWebLink(uri))
        {
            target = uri.AbsoluteUri;
            return true;
        }

        return false;
    }
}
