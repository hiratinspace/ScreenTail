using System.Security.Cryptography;
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
public sealed partial class UpdateService : IDisposable
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

    /// <summary>How long the loop waits after a check that could not finish, or while a download waits for a session to end.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>One check, and a download when it found something and no session is recording.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        await _checking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CheckOnceAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>
    /// Whether a session machine now in <paramref name="captureState"/> (its wire name) has stopped
    /// recording, so a download a session interrupted may continue: the host wakes the loop then.
    /// </summary>
    public static bool SessionOver(string captureState) =>
        captureState is Shared.Ipc.CaptureStates.Idle or Shared.Ipc.CaptureStates.DraftReady or Shared.Ipc.CaptureStates.DraftFailed;

    /// <summary>Checks again soon: the technician changed channel.</summary>
    public void CheckSoon()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    /// <summary>The loop the host runs. Starts checking only after <paramref name="policyFetched"/>.</summary>
    public async Task RunAsync(Task policyFetched, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policyFetched);
        RemoveStalePackages();
        if (Unusable() is { } never)
        {
            // Nothing will ever be checked by this build; say so once and stop.
            Set(never);
            return;
        }

        try
        {
            await policyFetched.WaitAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                var status = await CheckAsync(ct).ConfigureAwait(false);
                var wait = status.State switch
                {
                    UpdateState.Failed or UpdateState.Available or UpdateState.Blocked => RetryAfter,
                    _ => PolicyNow().Every ?? TimeSpan.FromHours(1),
                };
                _ = await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Removes downloaded packages, finished or not, whose version is no newer than the installed one:
    /// an update that has been installed, or one overtaken. Run as the loop starts.
    /// </summary>
    public void RemoveStalePackages()
    {
        try
        {
            if (!Directory.Exists(Folder.Path))
            {
                return;
            }

            // Prepared first, as every other use of the folder is: a folder that is a link is refused
            // (Prepare throws, and nothing is deleted), so a planted link cannot aim this elsewhere.
            Folder.Prepare();
            foreach (var file in Directory.EnumerateFiles(Folder.Path, "ScreenTail-*.msi*"))
            {
                var name = System.IO.Path.GetFileName(file);
                var bare = name.EndsWith(".msi.partial", StringComparison.Ordinal) ? name[..^".msi.partial".Length]
                    : name.EndsWith(".msi", StringComparison.Ordinal) ? name[..^".msi".Length]
                    : null;
                if (bare is not null && ReleaseVersion.TryParse(bare["ScreenTail-".Length..], out var version) && version <= Installed)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Installing, for the installer: the ready package is in Windows Installer's hands.</summary>
    internal void Installing() =>
        Set(new UpdateStatus(UpdateState.Installing, Status.AvailableVersion, $"Installing ScreenTail {Status.AvailableVersion}."));

    /// <summary>Windows Installer exited without installing; the package is still the verified one.</summary>
    internal void StillReady(string sentence)
    {
        if (Ready is { } ready)
        {
            Set(new UpdateStatus(UpdateState.Ready, ReleaseVersion.Format(ready.Manifest.Version), sentence));
        }
    }

    /// <summary>Windows Installer reported success and this process is still here to hear it.</summary>
    internal void InstalledNow(string sentence)
    {
        Ready = null;
        Set(new UpdateStatus(UpdateState.UpToDate, null, sentence));
    }

    /// <summary>
    /// The package that was checked failed a check just before it was installed: that file is deleted,
    /// and the refusal logged. Only that one: <see cref="Ready"/> is cleared only if it is still the
    /// package the installer checked (2026-10-29 review).
    /// </summary>
    internal void Discard(ReadyUpdate checkedPackage, string reason)
    {
        ArgumentNullException.ThrowIfNull(checkedPackage);
        TryDelete(checkedPackage.Path);
        if (Ready == checkedPackage)
        {
            Ready = null;
        }

        Refuse(reason);
    }

    /// <summary>
    /// The check's own lock, for the installer's check phase: a check that found a new release cannot
    /// replace the ready package while Install is verifying it.
    /// </summary>
    internal async Task<IDisposable> ExclusiveAsync(CancellationToken ct)
    {
        await _checking.WaitAsync(ct).ConfigureAwait(false);
        return new Exclusive(_checking);
    }

    /// <summary>The same lock, or null when a check still holds it after <paramref name="wait"/>.</summary>
    internal async Task<IDisposable?> TryExclusiveAsync(TimeSpan wait, CancellationToken ct) =>
        await _checking.WaitAsync(wait, ct).ConfigureAwait(false) ? new Exclusive(_checking) : null;

    private sealed class Exclusive(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private UpdateStatus? Unusable()
    {
        if (!Trust.Configured)
        {
            return new UpdateStatus(UpdateState.NotConfigured, null, "This build of ScreenTail has no update feed configured, so it does not check for updates.");
        }

        return SignedBuild
            ? null
            : new UpdateStatus(UpdateState.DevelopmentBuild, null, "This is a development build of ScreenTail, so it does not update itself.");
    }

    private async Task<UpdateStatus> CheckOnceAsync(CancellationToken ct)
    {
        if (Unusable() is { } never)
        {
            Set(never);
            return never;
        }

        if (Status.State == UpdateState.Installing)
        {
            return Status;
        }

        var policy = PolicyNow();
        if (policy.Every is null)
        {
            return Settle(new UpdateStatus(UpdateState.Off, null, "Your administrator turned off update checks. Your MSP installs updates with its own tools."));
        }

        if (policy.Channel is not { } channel)
        {
            return Settle(new UpdateStatus(UpdateState.ChannelUnknown, null, policy.Problem ?? UpdatePolicies.UnknownChannel));
        }

        Set(new UpdateStatus(UpdateState.Checking, Status.AvailableVersion, "Checking for updates…"));
        var fetched = await FetchManifestAsync(Trust.ManifestFor(channel, Architecture), ct).ConfigureAwait(false);
        if (fetched.Failure is { } failure)
        {
            return Settle(failure);
        }

        // Prepared before anything is read from or written in it, so on Windows the folder exists only
        // with its own permissions; nothing else in this class creates it (2026-10-29 review).
        try
        {
            Folder.Prepare();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Settle(Failed("The folder updates are downloaded into could not be used."));
        }

        var seen = Seen.Load(Folder.Path);
        var verdict = UpdateManifestReader.Read(fetched.Body, Trust, channel, Architecture, Installed, seen.For(channel));
        if (verdict.Outcome == ManifestOutcome.Refused)
        {
            if (verdict.Reason == UpdateManifestReader.Downgrade)
            {
                // Signed, verified, and only older than what is installed: a technician who moved from
                // pilot to stable while stable is behind. Nothing to install and nothing wrong, so it is
                // not a refusal and is not logged as one on every check.
                return Settle(new UpdateStatus(
                    UpdateState.UpToDate,
                    null,
                    $"ScreenTail {ReleaseVersion.Format(Installed)} is newer than the latest release on the {channel.ToWire()} channel. It updates once that channel catches up."));
            }

            return Refuse(verdict.Reason);
        }

        var manifest = verdict.Manifest!;
        seen.Remember(Folder.Path, channel, manifest.Version);
        if (verdict.Outcome == ManifestOutcome.UpToDate)
        {
            Ready = null;
            return Settle(new UpdateStatus(UpdateState.UpToDate, null, $"ScreenTail {ReleaseVersion.Format(Installed)} is up to date on the {channel.ToWire()} channel."));
        }

        var version = ReleaseVersion.Format(manifest.Version);
        if (Ready is { } ready && ready.Manifest == manifest && File.Exists(ready.Path))
        {
            return Settle(ReadyStatus(version));
        }

        if (SessionBusy())
        {
            // AC1: in the background, and not while a session is recording, so a customer's support call
            // never shares its connection with a download.
            return Settle(new UpdateStatus(UpdateState.Available, version, $"ScreenTail {version} is available. It downloads once no session is recording."));
        }

        Set(new UpdateStatus(UpdateState.Downloading, version, $"Downloading ScreenTail {version}…"));
        return await DownloadAsync(manifest, ct).ConfigureAwait(false);
    }

    private async Task<(byte[] Body, UpdateStatus? Failure)> FetchManifestAsync(Uri url, CancellationToken ct)
    {
        using var request = Net.EgressRequest.For(HttpMethod.Get, url, Net.EgressPurpose.UpdateCheck);
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return ([], Failed($"The update feed did not answer (HTTP {(int)response.StatusCode})."));
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[UpdateManifestReader.MaxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await body.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            // Too long to be a manifest is read as one byte too many, which the reader refuses.
            return (buffer[..read], null);
        }
        catch (Net.EgressBlockedException blocked)
        {
            return ([], new UpdateStatus(UpdateState.Blocked, null, Sentence(blocked)));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return ([], Failed("The update feed could not be reached. ScreenTail tries again later."));
        }
    }

    private async Task<UpdateStatus> DownloadAsync(UpdateManifest manifest, CancellationToken ct)
    {
        var version = ReleaseVersion.Format(manifest.Version);
        var final = System.IO.Path.Combine(Folder.Path, $"ScreenTail-{version}.msi");
        var partial = final + ".partial";
        var size = manifest.Package.Size;

        // What an earlier download that a session interrupted left, asked for from where it stopped. The
        // finished file's SHA-256 is checked over every byte either way, so a partial file that was
        // changed meanwhile only makes the download fail its check.
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have >= size)
        {
            TryDelete(partial);
            have = 0;
        }

        using var request = Net.EgressRequest.For(HttpMethod.Get, manifest.Package.Url, Net.EgressPurpose.UpdateCheck);
        if (have > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        }

        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // A resume is believed only as a 206 whose Content-Range starts at exactly the bytes kept and
            // totals exactly the manifest's size (second review). A 200 is the whole file again. Anything
            // else (416, a 206 at another offset, of another total or with no range) is a feed that cannot
            // be trusted to continue this file: the kept bytes go, nothing of the answer is written, and
            // the next check starts from zero.
            var range = response.Content.Headers.ContentRange;
            var resuming = have > 0
                && response.StatusCode == System.Net.HttpStatusCode.PartialContent
                && range is { HasRange: true, HasLength: true }
                && range.From == have
                && range.Length == size;
            if (have > 0 && !resuming && response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                TryDelete(partial);
                return Settle(Failed("The update feed did not continue the download where it stopped. It starts again at the next check."));
            }

            if (!resuming && response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return Settle(Failed($"The update could not be downloaded (HTTP {(int)response.StatusCode})."));
            }

            if (!resuming)
            {
                // A feed that ignored the range sends the whole file: start again rather than append it.
                have = 0;
            }

            if (response.Content.Headers.ContentLength is { } length && length != size - have)
            {
                TryDelete(partial);
                return Refuse("The downloaded package is not the size its manifest states, so it was not kept.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var total = have;
            var interrupted = false;
            await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))

            // Opened once, for reading the kept bytes into the hash and then appending after them, with
            // nobody else allowed in: no gap between hashing and writing for the file to change in.
            await using (var file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (resuming)
                {
                    var chunk = new byte[81920];
                    long read = 0;
                    int got;
                    while (read < have && (got = await file.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, have - read)), ct).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(chunk, 0, got);
                        read += got;
                    }

                    if (read != have)
                    {
                        throw new IOException("The kept part of the download changed while it was read.");
                    }

                    file.SetLength(have);
                    _ = file.Seek(have, SeekOrigin.Begin);
                }
                else
                {
                    file.SetLength(0);
                }

                var buffer = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > size)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);

                    // AC1 holds for a download already under way: a session that starts stops it, and
                    // what arrived is kept for the next check to continue from.
                    if (total < size && SessionBusy())
                    {
                        interrupted = true;
                        break;
                    }
                }
            }

            if (interrupted)
            {
                return Settle(new UpdateStatus(UpdateState.Available, version, $"ScreenTail {version} is available. It finishes downloading once no session is recording."));
            }

            if (total != size)
            {
                TryDelete(partial);
                return Refuse("The downloaded package is not the size its manifest states, so it was not kept.");
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), manifest.Package.Sha256, StringComparison.Ordinal))
            {
                TryDelete(partial);
                return Refuse("The downloaded package's SHA-256 is not the one its manifest states, so it was not kept.");
            }

            File.Move(partial, final, overwrite: true);
            RemoveOthers(final);
            Ready = new ReadyUpdate(manifest, final);
            return Settle(ReadyStatus(version));
        }
        catch (Net.EgressBlockedException blocked)
        {
            TryDelete(partial);
            return Settle(new UpdateStatus(UpdateState.Blocked, version, Sentence(blocked)));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            TryDelete(partial);
            return Settle(Failed("The update could not be downloaded. ScreenTail tries again later."));
        }
    }

    private static UpdateStatus ReadyStatus(string version) =>
        new(UpdateState.Ready, version, $"ScreenTail {version} is ready to install.");

    private static string Sentence(Net.EgressBlockedException blocked) =>
        blocked.Message.Contains("Local-only", StringComparison.Ordinal)
            ? "Local-only mode is on, so ScreenTail does not check for updates."
            : "Update checks are not allowed to reach the update feed from here.";

    private static UpdateStatus Failed(string sentence) => new(UpdateState.Failed, null, sentence);

    /// <summary>AC2: a refusal is logged with its reason, which is always one of our own sentences.</summary>
    private UpdateStatus Refuse(string reason)
    {
        if (Logger is { } logger)
        {
            LogRefused(logger, reason);
        }

        return Settle(new UpdateStatus(UpdateState.Refused, null, reason));
    }

    private UpdateStatus Settle(UpdateStatus status)
    {
        Set(status);
        return status;
    }

    private void RemoveOthers(string keep)
    {
        foreach (var file in Directory.EnumerateFiles(Folder.Path, "ScreenTail-*.msi*"))
        {
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _checking.Dispose();
        _wake.Dispose();
    }

    private void Set(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke(status);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update refused: {Reason}")]
    private static partial void LogRefused(ILogger logger, string reason);

    /// <summary>
    /// The newest version each channel has shown this installation, so an older manifest replayed later is
    /// refused. In the update folder; a technician who deletes it loses only this protection.
    /// </summary>
    private sealed class Seen
    {
        private const string FileName = "seen.json";

        private readonly Dictionary<string, string> _versions;

        private Seen(Dictionary<string, string> versions) => _versions = versions;

        public static Seen Load(string folder)
        {
            try
            {
                var path = System.IO.Path.Combine(folder, FileName);
                if (File.Exists(path)
                    && System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) is { } stored)
                {
                    return new Seen(stored);
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
            {
            }

            return new Seen(new Dictionary<string, string>(StringComparer.Ordinal));
        }

        public Version? For(UpdateChannel channel) =>
            _versions.TryGetValue(channel.ToWire(), out var text) && ReleaseVersion.TryParse(text, out var version) ? version : null;

        public void Remember(string folder, UpdateChannel channel, Version version)
        {
            if (For(channel) is { } known && known >= version)
            {
                return;
            }

            _versions[channel.ToWire()] = ReleaseVersion.Format(version);
            try
            {
                File.WriteAllText(System.IO.Path.Combine(folder, FileName), System.Text.Json.JsonSerializer.Serialize(_versions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
