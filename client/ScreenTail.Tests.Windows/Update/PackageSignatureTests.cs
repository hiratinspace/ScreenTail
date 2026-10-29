using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using ScreenTail.Core.Net;
using ScreenTail.Core.Update;
using ScreenTail.Platform.Ipc;
using ScreenTail.Service.Update;

namespace ScreenTail.Tests.Windows.Update;

/// <summary>
/// ST-113 on Windows: the publisher read by WinVerifyTrust through an open handle, on files Windows
/// really signs; the package write-locked while Windows Installer would run; msiexec by full path; and
/// the download folder's own permissions. No ScreenTail build is signed yet, so the signed sample is the
/// SDK's own dotnet.exe, which Microsoft signs with a timestamp the way ScreenTail's release will be.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PackageSignatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    public static bool OnWindows => OperatingSystem.IsWindows();

    private static string Dotnet => Path.Combine(
        Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
        "dotnet.exe");

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void ASignedTimestampedFileHasAPublisherReadThroughItsHandle()
    {
        Assert.True(File.Exists(Dotnet), "dotnet.exe is where the SDK puts it");
        using var file = new FileStream(Dotnet, FileMode.Open, FileAccess.Read, FileShare.Read);

        var publisher = new PackageSignature().Read(Dotnet, file.SafeFileHandle);

        Assert.NotNull(publisher);
        Assert.Contains("O=Microsoft Corporation", publisher.Subject, StringComparison.Ordinal);
        Assert.True(publisher.Timestamped, "Microsoft timestamps its signatures; the reader must see that");
        Assert.Matches("^[0-9A-F]{40}$", publisher.Thumbprint);
        Assert.Null(PublisherIdentity.Mismatch(publisher, publisher));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void TwoFilesFromOnePublisherAreTheSamePublisherWhateverTheirLeaf()
    {
        // The host resolver and the muxer are signed separately, sometimes on different days. Whether or
        // not their leaves differ, the identity is the publisher's and must compare equal.
        var fxr = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(Dotnet)!, "host", "fxr"), "hostfxr.dll", SearchOption.AllDirectories).First();
        var muxer = Authenticode.Publisher(Dotnet);
        var resolver = Authenticode.Publisher(fxr);

        Assert.NotNull(muxer);
        Assert.NotNull(resolver);
        Assert.Null(PublisherIdentity.Mismatch(muxer, resolver));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void ATamperedCopyAndAnUnsignedFileHaveNoPublisher()
    {
        Directory.CreateDirectory(_dir);
        var tampered = Path.Combine(_dir, "dotnet-tampered.exe");
        var bytes = File.ReadAllBytes(Dotnet);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(tampered, bytes);
        var unsigned = typeof(PackageSignatureTests).Assembly.Location;

        Assert.Null(Authenticode.Publisher(tampered));
        Assert.Null(Authenticode.Publisher(unsigned));
        Assert.NotNull(PublisherIdentity.Mismatch(Authenticode.Publisher(Dotnet), Authenticode.Publisher(tampered)));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void TheRunningProcessIsReadTheSameWay()
    {
        // Run as `dotnet <dll>`, the process is Microsoft's signed dotnet.exe; run through the test's own
        // unsigned apphost, it is a build nobody signed, which reads as no publisher at all.
        var running = PackageSignature.Running();

        if (string.Equals(Path.GetFileName(Environment.ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            Assert.NotNull(running);
            Assert.Contains("O=Microsoft Corporation", running.Subject, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(running);
        }
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void WindowsInstallerIsStartedByItsFullPathWithTheReviewedSwitches()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "msiexec.exe"), MsiLauncher.Msiexec, ignoreCase: true);
        Assert.True(File.Exists(MsiLauncher.Msiexec));
        Assert.Equal(["/i", @"C:\x\ScreenTail-0.1.20.msi", "/passive", "/norestart"], MsiLauncher.Arguments(@"C:\x\ScreenTail-0.1.20.msi"));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void TheDownloadFolderIsTheTechniciansSystemsAndAdministratorsOnly()
    {
        var folder = new WindowsUpdateFolder(Path.Combine(_dir, "Updates"));

        folder.Prepare();
        folder.Prepare();

        var security = new DirectoryInfo(folder.Path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "inheritance from the profile is cut");
        var who = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => r.IdentityReference.Value).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[] { WindowsIdentity.GetCurrent().User!.Value, "S-1-5-18", "S-1-5-32-544" }.Order(StringComparer.Ordinal),
            who);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void AFolderThatAlreadyExistsIsGivenTheSamePermissions()
    {
        // 2026-10-29 review: a folder made by anything else first (an older build, a plain
        // CreateDirectory) kept the profile's inherited permissions. Prepare applies them either way.
        var path = Path.Combine(_dir, "Updates");
        Directory.CreateDirectory(path);
        Assert.False(new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected);

        new WindowsUpdateFolder(path).Prepare();

        AssertOwnersOnly(path);
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public async Task WhileWindowsInstallerRunsThePackageCannotBeChangedMovedOrDeleted()
    {
        // The claim the installer rests on: the file it checked is the file Windows Installer opens. The
        // launcher here tries what anything running as the technician could try, while the real installer
        // holds its handle.
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var package = Encoding.ASCII.GetBytes(new string('m', 8192));
        var trust = new UpdateTrust(new Uri("https://updates.screentail.example/feed/"), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "downloads.screentail.example" }, [new UpdateKey("k", signer.ExportSubjectPublicKeyInfo())]);
        var feed = new Feed(Manifest(signer, package), package);
        var updatesFolder = Path.Combine(_dir, "Updates");
        using var updates = new UpdateService(
            new HttpClient(new EgressGuard(new EgressPolicy(ClientEgress.Settings(null, trust)), feed)),
            trust,
            new Version(0, 1, 10),
            new WindowsUpdateFolder(updatesFolder),
            () => new UpdatePolicy(UpdateChannel.Stable, false, TimeSpan.FromHours(24), false),
            () => false,
            signedBuild: true);
        Assert.Equal(UpdateState.Ready, (await updates.CheckAsync(TestContext.Current.CancellationToken)).State);
        AssertOwnersOnly(updatesFolder);
        var dotnet = Authenticode.Publisher(Dotnet);
        var launcher = new Probing(updatesFolder);
        var installer = new UpdateInstaller(updates, new SamePublisher(dotnet), launcher, dotnet, _ => Task.FromResult<IDisposable?>(new Nothing()));

        var answer = await installer.InstallAsync(TestContext.Current.CancellationToken);
        await installer.Finished;

        Assert.True(answer.Ok, answer.Refusal);
        Assert.Equal(["write refused", "rename refused", "delete refused", "folder rename refused", "read allowed"], launcher.Seen);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static void AssertOwnersOnly(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "inheritance from the profile is cut");
        var who = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Select(r => r.IdentityReference.Value).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[] { WindowsIdentity.GetCurrent().User!.Value, "S-1-5-18", "S-1-5-32-544" }.Order(StringComparer.Ordinal),
            who);
    }

    private static byte[] Manifest(ECDsa signer, byte[] package)
    {
        var payload = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"] = UpdateManifestReader.PayloadFormat,
            ["channel"] = "stable",
            ["architecture"] = "x64",
            ["version"] = "0.1.20",
            ["minimumVersion"] = "0.1.0",
            ["package"] = new JsonObject
            {
                ["url"] = "https://downloads.screentail.example/0.1.20/ScreenTail.msi",
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(package)),
                ["size"] = package.Length,
            },
            ["published"] = "2026-10-28T12:00:00Z",
        }.ToJsonString());
        return Encoding.UTF8.GetBytes(new JsonObject
        {
            ["format"] = UpdateManifestReader.EnvelopeFormat,
            ["key"] = "k",
            ["payload"] = Convert.ToBase64String(payload),
            ["signature"] = Convert.ToBase64String(signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)),
        }.ToJsonString());
    }

    private sealed class Feed(byte[] manifest, byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri!.Host.StartsWith("updates", StringComparison.Ordinal) ? manifest : package),
            });
    }

    private sealed class SamePublisher(PublisherIdentity? identity) : IPackagePublisher
    {
        public PublisherIdentity? Read(string path, SafeFileHandle file) => identity;
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class Probing(string folder) : IInstallerLauncher
    {
        public List<string> Seen { get; } = [];

        public Task<int> RunAsync(string package, CancellationToken ct = default)
        {
            Seen.Add(Try(() => new FileStream(package, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose(), "write"));
            Seen.Add(Try(() => File.Move(package, package + ".moved"), "rename"));
            Seen.Add(Try(() => File.Delete(package), "delete"));
            Seen.Add(Try(() => Directory.Move(folder, folder + "-moved"), "folder rename"));
            using (var read = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Seen.Add(read.ReadByte() >= 0 ? "read allowed" : "read empty");
            }

            return Task.FromResult(1602);
        }

        private static string Try(Action action, string what)
        {
            try
            {
                action();
                return what + " allowed";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return what + " refused";
            }
        }
    }
}
