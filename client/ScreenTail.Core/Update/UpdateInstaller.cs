using Microsoft.Win32.SafeHandles;

namespace ScreenTail.Core.Update;

/// <summary>Reads a package's Authenticode signer. On Windows, WinVerifyTrust through the open handle.</summary>
public interface IPackagePublisher
{
    /// <summary>The signer, when Windows accepts the signature and its chain; null otherwise.</summary>
    PublisherIdentity? Read(string path, SafeFileHandle file);
}

/// <summary>Starts Windows Installer on a package and reports its exit code when it finishes.</summary>
public interface IInstallerLauncher
{
    Task<int> RunAsync(string package, CancellationToken ct = default);
}

/// <param name="Refusal">Why nothing was started, for the technician.</param>
public sealed record InstallAnswer(bool Ok, string? Refusal);

/// <summary>
/// Installs a downloaded update when the technician asks (ST-113, ADR-0008).
///
/// <b>Never while a session is recording</b>, and nothing starts a session while Windows Installer runs:
/// both come from one step, <paramref name="holdSessions"/>, which the session machine answers under its
/// own transition lock (<c>SessionMachine.HoldStartsAsync</c>). It refuses while a session is under way
/// and, once granted, every start path (automatic, the hotkey, the pipe) is refused until it is released,
/// so a session cannot begin in the gap between the check and Windows Installer ending the service.
///
/// <b>Checked again immediately before it is launched</b>, because the package has been sitting in a
/// folder of the technician's since it was downloaded. The file is opened so that nobody can write,
/// rename or delete it while the handle is open; its size and SHA-256 are read through that handle and
/// compared with the verified manifest; Windows checks its signature through the same handle, and its
/// publisher must be the running service's publisher, by identity rather than by leaf certificate, with
/// a timestamp. Only then is Windows Installer started, and the handle is held until it exits, so what
/// it opens is what was checked.
///
/// The service has no administrator rights and does not get any: Windows Installer asks for them (UAC)
/// for the per-machine package, and a technician who cannot give them gets their MSP to push the update
/// instead.
/// </summary>
/// <param name="holdSessions">Holds new sessions back until disposed; null while a session is under way.</param>
public sealed class UpdateInstaller(
    UpdateService updates,
    IPackagePublisher publisher,
    IInstallerLauncher launcher,
    PublisherIdentity? running,
    Func<CancellationToken, Task<IDisposable?>> holdSessions)
{
    public const string DuringSession = "A session is recording. Finish it, then install the update.";

    public const string NothingReady = "No update is ready to install.";

    private readonly UpdateService _updates = updates ?? throw new ArgumentNullException(nameof(updates));
    private readonly IPackagePublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IInstallerLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    private readonly Func<CancellationToken, Task<IDisposable?>> _holdSessions = holdSessions ?? throw new ArgumentNullException(nameof(holdSessions));
    private readonly PublisherIdentity? _running = running;

    /// <summary>Windows Installer is running a package this started. No session may start meanwhile.</summary>
    public bool Installing { get; private set; }

    /// <summary>Finishes when Windows Installer has exited, after an install that started. For tests and the host's shutdown.</summary>
    public Task Finished { get; private set; } = Task.CompletedTask;

    public Task<InstallAnswer> InstallAsync(CancellationToken ct = default) => throw new NotImplementedException();
}
