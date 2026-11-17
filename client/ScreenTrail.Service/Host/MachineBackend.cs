using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using ScreenTrail.Core.Net;

namespace ScreenTrail.Service.Host;

/// <summary>
/// The backend address an administrator set for the whole machine (2026-11-15 review): the value the
/// installer writes from its <c>BACKENDURL</c> property, or that an MSP's own tooling writes, under
/// <c>HKEY_LOCAL_MACHINE\SOFTWARE\ScreenTrail</c>, which an ordinary user cannot change. Read from the
/// 64-bit view, where the 64-bit package writes it. <see cref="DeploymentAddresses"/> decides what it is
/// used for.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MachineBackend
{
    /// <summary>The value, or null when it is not set or cannot be read.</summary>
    public static string? Read()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(DeploymentAddresses.MachineKey);
            return key?.GetValue(DeploymentAddresses.MachineValue) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable is the same as unset: nothing is sent, and sessions are drafted on the device.
            return null;
        }
    }
}
