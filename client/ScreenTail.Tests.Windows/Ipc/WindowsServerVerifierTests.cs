using System.Runtime.Versioning;
using ScreenTail.Service.Ipc;

namespace ScreenTail.Tests.Windows.Ipc;

/// <summary>
/// ST-012 AC3 on Windows: "a modified service binary means the UI refuses to connect", which is the
/// executable rule read the other way round from <see cref="WindowsClientVerifierTests"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServerVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "screentail-tests", Guid.NewGuid().ToString("N"));

    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void ASignedUiAcceptsItsOwnPublisherAndNobodyElse()
    {
        var dotnet = Path.Combine(
            Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            "dotnet.exe");
        Assert.SkipUnless(File.Exists(dotnet), "dotnet.exe not found");
        var verifier = new WindowsServerVerifier(dotnet);
        Assert.True(verifier.UiIsSigned);

        Assert.Null(verifier.Verify(dotnet));
        Assert.Equal("unsigned_or_other_publisher", verifier.Verify(typeof(WindowsServerVerifierTests).Assembly.Location));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void ATamperedCopyOfASignedBinaryIsRefused()
    {
        // The reason WinVerifyTrust replaced a thumbprint comparison. This copies a genuinely signed
        // executable — certificate table and all — and changes one byte of its contents. The certificate
        // is still there and still has the right thumbprint; the signature no longer covers the file.
        //
        // Under the old check this file passed as "signed by our publisher", which is precisely the
        // modified binary ST-012 exists to refuse.
        var dotnet = Path.Combine(
            Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            "dotnet.exe");
        Assert.SkipUnless(File.Exists(dotnet), "dotnet.exe not found");

        Directory.CreateDirectory(_dir);
        var tampered = Path.Combine(_dir, "dotnet-tampered.exe");
        var bytes = File.ReadAllBytes(dotnet);

        // Somewhere in the code, well past the headers and well before the certificate table at the end.
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(tampered, bytes);

        var verifier = new WindowsServerVerifier(dotnet);

        Assert.True(verifier.UiIsSigned);
        Assert.Equal("unsigned_or_other_publisher", verifier.Verify(tampered));
    }

    [Fact(SkipUnless = nameof(OnWindows), Skip = "Needs Windows")]
    public void AnUnsignedDevBuildAcceptsSiblingsOnly()
    {
        Directory.CreateDirectory(_dir);
        var unsignedUi = Path.Combine(_dir, "ScreenTail.UI.exe");
        File.Copy(typeof(WindowsServerVerifierTests).Assembly.Location, unsignedUi);
        var sibling = Path.Combine(_dir, "ScreenTail.Service.exe");
        File.WriteAllBytes(sibling, [0x4D, 0x5A]);
        var verifier = new WindowsServerVerifier(unsignedUi);

        Assert.False(verifier.UiIsSigned);
        Assert.Null(verifier.Verify(sibling));
        Assert.Equal("dev_build_outside_ui_directory", verifier.Verify(Environment.ProcessPath!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
