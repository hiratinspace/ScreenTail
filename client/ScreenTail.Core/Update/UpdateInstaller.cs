using System.Security.Cryptography;
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
    Func<CancellationToken, Task<IDisposable?>> holdSessions,
    Action? sessionsReleased = null)
{
    public const string DuringSession = "A session is recording. Finish it, then install the update.";

    public const string NothingReady = "No update is ready to install.";

    private readonly UpdateService _updates = updates ?? throw new ArgumentNullException(nameof(updates));
    private readonly IPackagePublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IInstallerLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    private readonly Func<CancellationToken, Task<IDisposable?>> _holdSessions = holdSessions ?? throw new ArgumentNullException(nameof(holdSessions));
    private readonly PublisherIdentity? _running = running;
    private readonly Action _released = sessionsReleased ?? (() => { });

    /// <summary>Windows Installer is running a package this started. No session may start meanwhile.</summary>
    public bool Installing { get; private set; }

    /// <summary>Finishes when Windows Installer has exited, after an install that started. For tests and the host's shutdown.</summary>
    public Task Finished { get; private set; } = Task.CompletedTask;

    private readonly Lock _gate = new();

    public async Task<InstallAnswer> InstallAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (Installing)
            {
                return new InstallAnswer(false, "The update is already being installed.");
            }

            // Claimed now, so a second press while this one is checking is refused rather than run twice.
            Installing = true;
        }

        IDisposable? hold = null;
        FileStream? package = null;
        var started = false;
        try
        {
            if (_updates.Status.State != UpdateState.Ready || _updates.Ready is not { } ready)
            {
                return Refused(NothingReady);
            }

            if (_running is null)
            {
                return Refused(PublisherIdentity.Mismatch(null, null)!);
            }

            hold = await _holdSessions(ct).ConfigureAwait(false);
            if (hold is null)
            {
                return Refused(DuringSession);
            }

            // Opened so that nothing can write, rename or delete it while the handle is open: what is
            // checked here is what Windows Installer reads.
            try
            {
                package = new FileStream(ready.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Discarded("The downloaded update could not be opened to be checked, so it was not installed.");
            }

            if (package.Length != ready.Manifest.Package.Size)
            {
                return await DiscardAsync(package, "The downloaded update changed size since it was downloaded, so it was not installed.").ConfigureAwait(false);
            }

            var hash = await SHA256.HashDataAsync(package, ct).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexStringLower(hash), ready.Manifest.Package.Sha256, StringComparison.Ordinal))
            {
                return await DiscardAsync(package, "The downloaded update changed since it was downloaded, so it was not installed.").ConfigureAwait(false);
            }

            if (PublisherIdentity.Mismatch(_running, _publisher.Read(ready.Path, package.SafeFileHandle)) is { } stranger)
            {
                return await DiscardAsync(package, stranger).ConfigureAwait(false);
            }

            _updates.Installing();
            Finished = AfterAsync(_launcher.RunAsync(ready.Path, CancellationToken.None), package, hold);
            started = true;
            return new InstallAnswer(true, null);
        }
        finally
        {
            if (!started)
            {
                if (package is not null)
                {
                    await package.DisposeAsync().ConfigureAwait(false);
                }

                hold?.Dispose();
                lock (_gate)
                {
                    Installing = false;
                }
            }
        }
    }

    /// <summary>Holds the file and the sessions until Windows Installer has gone, then says how it went.</summary>
    private async Task AfterAsync(Task<int> exited, FileStream package, IDisposable hold)
    {
        int code;
        try
        {
            code = await exited.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            code = -1;
        }
        finally
        {
            await package.DisposeAsync().ConfigureAwait(false);
            hold.Dispose();
        }

        lock (_gate)
        {
            Installing = false;
        }

        // Most often this process does not see it: Windows Installer ends it to replace it.
        if (code is 0 or 3010)
        {
            _updates.InstalledNow("The update was installed. ScreenTail starts again at your next sign-in.");
            return;
        }

        _updates.StillReady(code switch
        {
            1602 or 1223 => "The update was cancelled at the administrator prompt. It is still ready to install.",
            _ => $"Windows Installer stopped without installing the update (code {code}). It is still ready to install.",
        });
    }

    private static InstallAnswer Refused(string reason) => new(false, reason);

    /// <summary>Closes the package first: Windows will not delete a file this process holds open.</summary>
    private async Task<InstallAnswer> DiscardAsync(FileStream package, string reason)
    {
        await package.DisposeAsync().ConfigureAwait(false);
        return Discarded(reason);
    }

    private InstallAnswer Discarded(string reason)
    {
        _updates.Discard(reason);
        return new InstallAnswer(false, reason);
    }
}
