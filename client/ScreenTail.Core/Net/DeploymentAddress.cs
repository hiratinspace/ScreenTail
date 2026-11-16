namespace ScreenTail.Core.Net;

/// <summary>Where the backend address in force came from, for the service's start-up log. Never the address itself.</summary>
public enum AddressOrigin
{
    /// <summary>None was configured: nothing is sent, and sessions are drafted on the device.</summary>
    None,

    /// <summary>The machine-wide setting an administrator wrote (the installer's <c>BACKENDURL</c>).</summary>
    Machine,

    /// <summary><c>SCREENTAIL_BACKEND</c>: development builds, the demo and the end-to-end run only.</summary>
    Environment,
}

/// <param name="Backend">The address, or null when none was configured.</param>
/// <param name="Origin">Where it came from.</param>
/// <param name="EnvironmentIgnored">An installed build found the environment variables set and did not use them.</param>
/// <param name="AdministratorNote">
/// What the technician is told when an installed build has no machine-wide address: only an administrator
/// can set it, so the device card, an activation and diagnostics say so rather than a bare "no backend".
/// Null otherwise.
/// </param>
public sealed record DeploymentAddress(Uri? Backend, AddressOrigin Origin, bool EnvironmentIgnored, string? AdministratorNote = null);

/// <summary>
/// Where the capture service learns its backend and, in development, its device token (2026-11-15 review).
///
/// The address decides where the egress allowlist points and where the tenant's policy is fetched from,
/// so a locked local-only is only as strong as the place the address is read from. A per-user
/// environment variable is a setting the technician, or anything running as them, can change; an
/// installed build reads the address only from the machine-wide setting, which only an administrator
/// can write, and ignores the variables. Development builds (anything not running from the install folder)
/// keep the variables, because the test loop, the demo and the end-to-end run are built on them, with
/// the machine setting as their fallback.
///
/// The same rule holds for <c>SCREENTAIL_DEVICE_TOKEN</c>: an installed build is enrolled with an
/// invite's code (ST-010) and never with a token from its environment.
///
/// In Core, so every platform's suite runs it; the service reads the registry and the signature.
/// </summary>
public static class DeploymentAddresses
{
    /// <summary>The backend address for development builds.</summary>
    public const string BackendVariable = "SCREENTAIL_BACKEND";

    /// <summary>The device token for development builds (the M1 runbook's, and the demo's).</summary>
    public const string DeviceTokenVariable = "SCREENTAIL_DEVICE_TOKEN";

    /// <summary>The machine-wide key the installer writes, under HKEY_LOCAL_MACHINE (64-bit view).</summary>
    public const string MachineKey = @"SOFTWARE\ScreenTail";

    /// <summary>The value under <see cref="MachineKey"/> holding the backend address.</summary>
    public const string MachineValue = "BackendUrl";

    /// <summary>An installed build with no machine-wide address: the technician cannot fix it, an administrator can.</summary>
    public const string NoMachineAddress =
        "This computer has no backend address set, so sessions are drafted here and nothing can be published. Ask your administrator to set it (BACKENDURL when installing ScreenTail).";

    /// <summary>Added when the same build also found SCREENTAIL_BACKEND set, which it does not read.</summary>
    public const string EnvironmentNotRead = " An installed copy does not read SCREENTAIL_BACKEND.";

    /// <param name="installed">Running from the install folder under Program Files (<see cref="InstalledAt"/>).</param>
    /// <param name="machine">The machine-wide setting, or null when there is none.</param>
    /// <param name="environment">Reads an environment variable by name.</param>
    public static DeploymentAddress Resolve(bool installed, string? machine, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var fromMachine = Parse(machine);
        if (installed)
        {
            var set = !string.IsNullOrEmpty(environment(BackendVariable)) || !string.IsNullOrEmpty(environment(DeviceTokenVariable));
            if (fromMachine is null)
            {
                var note = string.IsNullOrEmpty(environment(BackendVariable)) ? NoMachineAddress : NoMachineAddress + EnvironmentNotRead;
                return new DeploymentAddress(null, AddressOrigin.None, set, note);
            }

            return new DeploymentAddress(fromMachine, AddressOrigin.Machine, set);
        }

        if (Parse(environment(BackendVariable)) is { } fromEnvironment)
        {
            return new DeploymentAddress(fromEnvironment, AddressOrigin.Environment, false);
        }

        return new DeploymentAddress(fromMachine, fromMachine is null ? AddressOrigin.None : AddressOrigin.Machine, false);
    }

    /// <summary>The development fallback token, or null for an installed build, which never takes one from its environment.</summary>
    public static Func<string?>? FallbackToken(bool installed, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return installed ? null : () => environment(DeviceTokenVariable);
    }

    /// <summary>The folder the installer puts ScreenTail in, under the machine's 64-bit Program Files.</summary>
    public const string InstallFolderName = "ScreenTail";

    /// <summary>
    /// True when <paramref name="executable"/> sits inside <c>ScreenTail</c> under
    /// <paramref name="programFiles"/>: the one folder the package installs into (its RefuseInstallFolder
    /// step refuses any other) and one only an administrator can write. Decided by location and not by
    /// signature: any Authenticode signer would pass a signature check, the .NET host under Program Files
    /// included, and there is no shipped publisher identity to compare against yet.
    /// </summary>
    /// <param name="programFiles">
    /// The machine's Program Files, from the known folder (<c>Environment.SpecialFolder.ProgramFiles</c> in
    /// a 64-bit process), never from <c>%ProgramW6432%</c>: a per-user variable can override that one.
    /// </param>
    public static bool InstalledAt(string? executable, string? programFiles)
    {
        if (string.IsNullOrEmpty(executable) || string.IsNullOrEmpty(programFiles))
        {
            return false;
        }

        var full = Path.GetFullPath(executable);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(programFiles, InstallFolderName))) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? Parse(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var url) ? url : null;
}
