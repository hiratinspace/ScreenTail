using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ScreenTail.Core.Update;

namespace ScreenTail.Service.Update;

/// <summary>
/// Starts Windows Installer on a verified package (ST-113, ADR-0008).
///
/// <c>msiexec.exe</c> from System32 by its full path, started with the technician's own rights and no
/// shell, so nothing on the PATH or in a file association decides what runs. <c>/passive</c> shows
/// progress and nothing to click; for the per-machine package Windows Installer itself then asks for an
/// administrator's approval (UAC), and that prompt names the package and its signer. <c>/norestart</c>
/// because ScreenTail never restarts a technician's machine.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MsiLauncher : IInstallerLauncher
{
    public static string Msiexec => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

    public static IReadOnlyList<string> Arguments(string package) => ["/i", package, "/passive", "/norestart"];

    public async Task<int> RunAsync(string package, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        var start = new ProcessStartInfo(Msiexec) { UseShellExecute = false, CreateNoWindow = false };
        foreach (var argument in Arguments(package))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows Installer did not start.");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }
}

/// <summary>
/// The folder a package waits in: under the technician's own data folder, created so that only they,
/// the system account and administrators can reach it, with inheritance cut, and refused when it is a
/// link (ST-113). What protects the package between this and Windows Installer is the installer's
/// write-locked handle and its checks; this keeps the folder from being anyone else's to start with.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUpdateFolder(string path) : IUpdateFolder
{
    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public void Prepare()
    {
        var folder = new DirectoryInfo(Path);
        if (!folder.Exists)
        {
            folder.Create(Security());
            folder.Refresh();
        }

        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The update folder is a link.");
        }

        // A folder that was already there (an older build, or anything else that made it first) gets the
        // same permissions, every time, rather than keeping whatever it was made with (2026-10-29 review).
        folder.SetAccessControl(Security());
    }

    internal static DirectorySecurity Security()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var who in new IdentityReference[]
        {
            WindowsIdentity.GetCurrent().User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }
}
