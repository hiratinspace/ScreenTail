using Microsoft.Extensions.Logging;

namespace ScreenTail.Core.Update;

/// <summary>Where an installation stands with updates (ST-113). Each has one sentence for Settings.</summary>
public enum UpdateState
{
    /// <summary>This build has no feed or no key compiled in. No request is ever made.</summary>
    NotConfigured,

    /// <summary>Nothing signed this build, so there is no publisher to compare a package with. No request is made.</summary>
    DevelopmentBuild,

    /// <summary>The tenant's admin turned update checks off.</summary>
    Off,

    /// <summary>The admin chose a channel this version does not know.</summary>
    ChannelUnknown,

    /// <summary>The egress guard refused the check, usually because local-only mode is on.</summary>
    Blocked,

    /// <summary>Not checked yet in this run.</summary>
    Waiting,

    Checking,

    UpToDate,

    /// <summary>A newer release, verified, waiting for no session to be recording before it downloads.</summary>
    Available,

    Downloading,

    /// <summary>Downloaded and verified; the technician can install it.</summary>
    Ready,

    /// <summary>Windows Installer is running it.</summary>
    Installing,

    /// <summary>The manifest or the package failed a check. Logged with the reason.</summary>
    Refused,

    /// <summary>The feed or the download did not answer. Tried again later.</summary>
    Failed,
}

/// <param name="AvailableVersion">The verified newer version, once there is one.</param>
/// <param name="Sentence">For Settings. Never quotes a manifest that has not verified.</param>
public sealed record UpdateStatus(UpdateState State, string? AvailableVersion, string Sentence);

/// <summary>A downloaded package whose size and SHA-256 matched its verified manifest when it was saved.</summary>
public sealed record ReadyUpdate(UpdateManifest Manifest, string Path);

/// <summary>
/// The folder a package is downloaded into. The service's own data folder under the technician's
/// profile, which only they (and administrators) can write: the Windows implementation sets that
/// explicitly and refuses a folder reached through a link.
/// </summary>
public interface IUpdateFolder
{
    string Path { get; }

    /// <summary>Creates the folder if it is missing. Throws <see cref="IOException"/> when it cannot be used.</summary>
    void Prepare();
}

/// <summary>A plain folder, for the Mac suite and for anything that is not Windows.</summary>
public sealed class PlainUpdateFolder(string path) : IUpdateFolder
{
    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public void Prepare()
    {
        var folder = Directory.CreateDirectory(Path);
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The update folder is a link.");
        }
    }
}

/// <summary>
/// Checks the update feed, downloads a newer release in the background and holds it ready (ST-113,
/// ADR-0008).
///
/// <b>What it sends</b> is one GET for <c>&lt;feed&gt;&lt;channel&gt;/win-x64.json</c>, under its own
/// egress purpose, with no credential, no cookie, no device or tenant and no body; then, for a verified
/// newer release, one GET for the package. Both go through the egress guard, so local-only stops them
/// unless the tenant's admin allowed update checks (INV-8, INV-11).
///
/// <b>What it believes</b> is only a manifest whose signature verifies with a key compiled into this
/// client (<see cref="UpdateManifestReader"/>), and only a package whose size and SHA-256 match that
/// manifest. A refusal is logged with its reason and changes nothing else (ticket AC2).
///
/// <b>When</b>: never before this run has fetched the tenant's policy once, because until then the
/// policy in force is a cached file the technician can edit; then at the admin's interval. A download
/// waits until no session is recording, and so does everything the technician is shown about it.
/// </summary>
public sealed partial class UpdateService
{
    public const string Architecture = "x64";

    public UpdateService(
        HttpClient http,
        UpdateTrust trust,
        Version installed,
        IUpdateFolder folder,
        Func<UpdatePolicy> policy,
        Func<bool> sessionBusy,
        bool signedBuild,
        ILogger? logger = null,
        TimeProvider? time = null)
    {
        Http = http ?? throw new ArgumentNullException(nameof(http));
        Trust = trust ?? throw new ArgumentNullException(nameof(trust));
        Installed = installed ?? throw new ArgumentNullException(nameof(installed));
        Folder = folder ?? throw new ArgumentNullException(nameof(folder));
        PolicyNow = policy ?? throw new ArgumentNullException(nameof(policy));
        SessionBusy = sessionBusy ?? throw new ArgumentNullException(nameof(sessionBusy));
        SignedBuild = signedBuild;
        Logger = logger;
        Time = time ?? TimeProvider.System;
        Status = new UpdateStatus(UpdateState.Waiting, null, "ScreenTail has not checked for updates yet.");
    }

    public Version Installed { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes, on whatever thread changed it.</summary>
    public event Action<UpdateStatus>? Changed;

    public UpdateStatus Status { get; private set; }

    /// <summary>The package to install, while <see cref="Status"/> is <see cref="UpdateState.Ready"/>.</summary>
    public ReadyUpdate? Ready { get; private set; }

    private HttpClient Http { get; }

    private UpdateTrust Trust { get; }

    private IUpdateFolder Folder { get; }

    private Func<UpdatePolicy> PolicyNow { get; }

    private Func<bool> SessionBusy { get; }

    private bool SignedBuild { get; }

    private ILogger? Logger { get; }

    private TimeProvider Time { get; }

    /// <summary>One check, and a download when it found something and no session is recording.</summary>
    public Task<UpdateStatus> CheckAsync(CancellationToken ct = default) => throw new NotImplementedException();

    /// <summary>The loop the host runs. Starts checking only after <paramref name="policyFetched"/>.</summary>
    public Task RunAsync(Task policyFetched, CancellationToken ct) => throw new NotImplementedException();

    private void Set(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke(status);
    }
}
