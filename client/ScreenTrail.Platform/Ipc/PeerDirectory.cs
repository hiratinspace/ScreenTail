using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenTrail.Platform.Ipc;

/// <summary>
/// Whether two executables sit in the same folder, the second half of both peer checks (ADR-0003).
///
/// A signature covers one file. An executable also loads libraries from its own folder, and those are
/// not covered by its signature, so "signed by us" says nothing about a signed copy run from a folder the
/// user can write to. The installer puts the service and the UI side by side in Program Files, which only
/// an administrator can change, so each end requires the other to be there too (2026-11-15 review).
///
/// <b>Compared as text, and faithfully.</b> The peer's path is the kernel's record of what it was started
/// from, and it is read as written. Windows' own path clean-up is not applied to it: that clean-up drops
/// a trailing dot or space from a folder name, and a folder named that way is a different folder, one an
/// ordinary user can make at the root of a drive. Separators, <c>.</c> and <c>..</c> are tidied here by
/// hand, which loses nothing, because no folder can have those names.
///
/// <b>Links are not followed.</b> Following one would let a folder the user controls stand in for ours,
/// by pointing a junction at the install folder once the peer is running. A short (8.3) folder name is
/// expanded to its long form, which names the same folder and never another one.
///
/// <b>Case</b> is ignored for ASCII letters only. Everything else must match exactly, so a name that some
/// casing table would fold onto ours is refused rather than accepted. A refusal costs a reconnect; an
/// acceptance of the wrong folder costs the check.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PeerDirectory
{
    /// <summary>
    /// The path a verifier compares its peers against. When it is this process's own executable, it is
    /// read from the kernel, where the peer's path comes from too. <c>Environment.ProcessPath</c> is the
    /// path as launched, and through a junction or a mounted folder that is spelt differently from the
    /// kernel's record of the same file, so a signed install would have refused its own other half.
    /// Any other path (a test's anchor) is taken as given.
    /// </summary>
    internal static string Anchor(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var full = Path.GetFullPath(executable);
        var own = Environment.ProcessPath;
        return own is not null
            && string.Equals(Path.GetFullPath(own), full, StringComparison.OrdinalIgnoreCase)
            && ProcessImage.OwnPath() is { } kernel
            ? kernel
            : full;
    }

    /// <summary>Whether <paramref name="first"/> and <paramref name="second"/> are in the same folder.</summary>
    internal static bool Same(string first, string second) => SameText(Of(first), Of(second));

    /// <summary>The folder an executable is in, in the one form <see cref="Same"/> compares.</summary>
    internal static string Of(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var path = WithoutDevicePrefix(executable).Replace('/', '\\');

        // Only a caller's relative path takes this, never a process's image, which is always absolute.
        if (!Path.IsPathFullyQualified(path))
        {
            path = Path.GetFullPath(path);
        }

        var root = Path.GetPathRoot(path) ?? string.Empty;
        var segments = new List<string>();
        foreach (var segment in path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    break;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        // The last segment is the executable's own name.
        if (segments.Count > 0)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        var folder = root.TrimEnd('\\') + "\\" + string.Join('\\', segments);
        if (folder.Contains('~', StringComparison.Ordinal))
        {
            folder = LongForm(folder);
        }

        return Path.TrimEndingDirectorySeparator(folder);
    }

    /// <summary>
    /// Ordinal, with ASCII letters compared case-insensitively and nothing else folded. Windows paths are
    /// case-insensitive, and the same folder can arrive as <c>C:\Program Files</c> from one source and
    /// <c>c:\program files</c> from another.
    /// </summary>
    internal static bool SameText(string first, string second)
    {
        if (first.Length != second.Length)
        {
            return false;
        }

        for (var i = 0; i < first.Length; i++)
        {
            var a = first[i];
            var b = second[i];
            if (a != b && !(char.IsAsciiLetter(a) && char.IsAsciiLetter(b) && (a | 0x20) == (b | 0x20)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>\\?\C:\...</c> and <c>\\?\UNC\server\...</c> as the plain paths they name, so the same folder is
    /// not two strings. Any other device path (a volume GUID, say) is left alone and matches nothing.
    /// </summary>
    private static string WithoutDevicePrefix(string path)
    {
        foreach (var prefix in new[] { @"\\?\", @"\\.\" })
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = path[prefix.Length..];
            if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + rest[4..];
            }

            if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':')
            {
                return rest;
            }
        }

        return path;
    }

    /// <summary>
    /// The folder with any short names expanded, or as given when Windows cannot say. Asked with the
    /// <c>\\?\</c> prefix, so Windows looks up the folder as written rather than its cleaned-up name.
    /// </summary>
    private static string LongForm(string folder)
    {
        var literal = folder.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + folder[2..] : @"\\?\" + folder;
        var buffer = new char[32_768];
        var length = GetLongPathName(literal, buffer, (uint)buffer.Length);
        return length is > 0 and < 32_768 ? WithoutDevicePrefix(new string(buffer, 0, (int)length)) : folder;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, [Out] char[] longPath, uint bufferLength);
}
