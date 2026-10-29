using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ScreenTail.Core.Update;

/// <summary>
/// Which releases this installation follows (ST-113). Stable is what every installation gets unless
/// someone chooses otherwise; pilot gets each release first, for the technicians who agreed to try it.
/// </summary>
public enum UpdateChannel
{
    Stable,
    Pilot,
}

/// <summary>The channels' names on the wire, in the policy and in the feed's paths.</summary>
public static class UpdateChannels
{
    public const string Stable = "stable";

    public const string Pilot = "pilot";

    public static string ToWire(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Pilot => Pilot,
        _ => Stable,
    };

    /// <summary>Exactly "stable" or "pilot". Anything else is not a channel, rather than a guess at one.</summary>
    public static bool TryParse(string? wire, out UpdateChannel channel)
    {
        switch (wire)
        {
            case Stable:
                channel = UpdateChannel.Stable;
                return true;
            case Pilot:
                channel = UpdateChannel.Pilot;
                return true;
            default:
                channel = UpdateChannel.Stable;
                return false;
        }
    }
}

/// <summary>
/// A release's version: three whole numbers, nothing else (ST-112 builds every package as 0.1.N).
///
/// Strict on purpose. <see cref="Version.TryParse(string?, out Version?)"/> accepts two to four parts,
/// leading signs and spaces, and a manifest is the one input here that decides whether a program is
/// installed, so it is read one way only.
/// </summary>
public static class ReleaseVersion
{
    public static bool TryParse(string? text, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text) || text.Length > 32)
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > 9 || !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            numbers[i] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>How a version is written: three parts, whatever the <see cref="Version"/> carries.</summary>
    public static string Format(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}");
    }
}
