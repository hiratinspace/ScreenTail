using System.Security.Cryptography;
using ScreenTail.Shared.Ipc;

namespace ScreenTail.Core.Ipc;

/// <summary>The per-service-run token (ADR-0003): 32 random bytes, hex on the wire, compared in constant time.</summary>
public static class IpcToken
{
    public const int Length = 32;

    public static byte[] Generate() => RandomNumberGenerator.GetBytes(Length);

    public static string Encode(byte[] token) => Convert.ToHexString(token);

    public static bool Matches(byte[] expected, string presented)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (presented is null || presented.Length != expected.Length * 2)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(presented), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// Where the service leaves the token for the UI: a file only this user can read
/// (<c>%LOCALAPPDATA%\ScreenTail\ipc.token</c> on Windows, mode 0600 elsewhere).
/// </summary>
public static class IpcTokenFile
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScreenTail",
        "ipc.token");

    public static void Write(string path, byte[] token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write beside, restrict, then move into place so a reader never sees a half-written or open file.
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, token);
        RestrictToCurrentUser(temp);
        File.Move(temp, path, overwrite: true);
    }

    public static byte[] Read(string path)
    {
        var token = File.ReadAllBytes(path);
        if (token.Length != IpcToken.Length)
        {
            throw new IpcProtocolException($"The token file holds {token.Length} bytes; {IpcToken.Length} expected.");
        }

        return token;
    }

    private static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictOnWindows(path);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictOnWindows(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot determine the current Windows user.");
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            user,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
