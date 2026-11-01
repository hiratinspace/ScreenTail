using ScreenTail.Core.Install;

namespace ScreenTail.Core.Lifecycle;

/// <summary>
/// What the capture service tells Windows about being restarted (ST-112, RegisterApplicationRestart).
///
/// Since the service answers Restart Manager, any program's installer that needs a file the service has
/// loaded can close it: a silent install of the Visual C++ runtime, which Whisper's libraries load, is
/// enough. Before, such an installer could not close the service and left its files for a restart, so the
/// service kept running. Registering for restart keeps that: Restart Manager starts the service again once
/// the installer is done, with <see cref="CommandLine"/>, which is the ordinary start at sign-in. That start
/// is watching, not recording: a session the close interrupted is recovered as partial (ST-020), never
/// resumed, and a new one begins only the way any session begins. The single-instance mutex covers a
/// restart that finds a copy already running.
///
/// Only Restart Manager restarts it: not after a crash, a hang or a reboot (<see cref="Flags"/>), so
/// nothing here brings capture back that the technician did not have. The one-shot modes never register:
/// they are not the service, and restarting one would run it again.
/// </summary>
public static class ApplicationRestart
{
    /// <summary>RESTART_NO_CRASH: Windows Error Reporting does not restart it after a crash.</summary>
    public const int RestartNoCrash = 0x1;

    /// <summary>RESTART_NO_HANG: nor after it hung.</summary>
    public const int RestartNoHang = 0x2;

    /// <summary>RESTART_NO_REBOOT: nor after Windows restarts for an update; the Run key starts it at sign-in.</summary>
    public const int RestartNoReboot = 0x8;

    /// <summary>Restart Manager's restart after an installation only.</summary>
    public const int Flags = RestartNoCrash | RestartNoHang | RestartNoReboot;

    /// <summary>No arguments: the restart is the service's ordinary start, exactly as the Run key starts it.</summary>
    public const string CommandLine = "";

    /// <summary>The flags of the modes that run once and exit, the installer's two steps among them.</summary>
    public static readonly IReadOnlyList<string> OneShotModes = [InstalledFootprint.UninstallFlag, InstalledFootprint.UpgradeReleaseFlag, "--capabilities", "--transcribe"];

    /// <summary>Whether a process started with these arguments is the service, and so registers for restart.</summary>
    public static bool ShouldRegister(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return !args.Any(arg => OneShotModes.Contains(arg, StringComparer.Ordinal));
    }
}
