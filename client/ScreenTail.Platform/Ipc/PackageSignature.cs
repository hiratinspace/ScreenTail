using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using ScreenTail.Core.Update;

namespace ScreenTail.Platform.Ipc;

/// <summary>
/// The Authenticode half of ST-113's "same publisher" check: who signed a downloaded package, read by
/// <c>WinVerifyTrust</c> through the handle the installer holds open, and who signed the running service.
///
/// Here rather than in the service because it is Authenticode, which lives here beside the peer checks
/// (ADR-0005). Only the identity is returned; <see cref="PublisherIdentity.Mismatch"/> compares it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PackageSignature : IPackagePublisher
{
    /// <summary>The running process's own signer, or null for a build nobody signed.</summary>
    public static PublisherIdentity? Running()
    {
        var image = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrEmpty(image) ? null : Authenticode.Publisher(image);
    }

    public PublisherIdentity? Read(string path, SafeFileHandle file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(file);
        return Authenticode.Publisher(path, file);
    }
}
